using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Cli.Serve;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Ui;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>tx ui</c>: holds a live session and shares it on localhost (#369), so the browser page and
/// agents (<c>tx serve</c>, <c>tx mcp</c>) work on the same open model. A second <c>tx ui</c> on the
/// same model prints the running session's URL instead of opening another.
/// </summary>
internal sealed class UiCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly CliStateStore _state;
    private readonly StagingStore _staging;
    private readonly string _version;
    private readonly Func<SessionScope?, IEnumerable<Command>, RootCommand> _buildSessionRoot;
    private readonly LiveRegistry _registry;
    private readonly Func<string, bool> _openBrowser;

    /// <param name="buildSessionRoot">Builds the command tree the session's methods run, as for <c>tx serve</c>.</param>
    /// <param name="registry">Where live sessions are recorded; the user's by default.</param>
    /// <param name="openBrowser">Opens a URL, returning whether it could; the system browser by default.</param>
    public UiCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        StagingStore staging,
        string version,
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildSessionRoot,
        LiveRegistry? registry = null,
        Func<string, bool>? openBrowser = null)
    {
        _providers = providers;
        _state = state;
        _staging = staging;
        _version = version;
        _buildSessionRoot = buildSessionRoot;
        _registry = registry ?? LiveRegistry.Default;
        _openBrowser = openBrowser ?? OpenBrowser;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Model to open: a TMDL folder or .bim file (default: the active connection's)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var portOption = new Option<int>("--port")
        {
            Description = "Port on 127.0.0.1 (default: a free one)",
            HelpName = "port"
        };
        portOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 0 or > 65535)
                result.AddError("--port must be between 0 and 65535; 0 picks a free port.");
        });
        var openOption = new Option<bool>("--open")
        {
            Description = "Open the page in the default browser"
        };
        var graceOption = new Option<int>("--grace")
        {
            Description = "Seconds to keep running after the last client leaves (default: 30)",
            HelpName = "seconds",
            DefaultValueFactory = _ => 30
        };
        graceOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
                result.AddError("--grace must be 0 or more.");
        });
        var logOption = new Option<string>("--log")
        {
            Description = "Append the session's request log to this file",
            HelpName = "file"
        };

        var command = new Command("ui", "Share a model session with a browser and agents")
        {
            modelArgument,
            portOption,
            openOption,
            graceOption,
            logOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var opener = new SessionOpener(_providers, _state, _staging);
            var explicitModel = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            if (!opener.TryResolve(parseResult, explicitModel, out var reference, out _, out var resolveExit))
                return resolveExit;
            var json = OutputFormats.IsJson(GlobalOptions.OutputFormatValue(parseResult));
            var open = parseResult.GetValue(openOption);

            // One session per model: a second 'tx ui' hands out the running one.
            if (!string.IsNullOrWhiteSpace(reference.Value) && _registry.Find(reference.Value) is { } running)
            {
                var runningUrl = PageUrl(running.BaseUrl, running.Token);
                Announce(Console.Out, json, runningUrl, running.Port, running.Model, running.ProcessId, joined: true);
                if (!json)
                    StdErr.MarkupLine(Styling.Guidance($"{running.Model} is already open in tx ui (process {running.ProcessId})."));
                if (open)
                    Open(runningUrl, Console.Error);
                return 0;
            }

            var (session, openExit) = await opener.OpenAsync(parseResult, reference, cancellationToken);
            if (session is null)
                return openExit;

            var logPath = parseResult.GetValue(logOption);
            await using var logFile = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
            var log = TextWriter.Synchronized(logFile ?? TextWriter.Null);
            var host = new SessionHost(_buildSessionRoot, opener, log, session);
            var token = UiHost.NewToken();
            UiHost endpoint;
            try
            {
                endpoint = await WebEndpoint.StartAsync(host, _version, parseResult.GetValue(portOption), token, cancellationToken);
            }
            catch (IOException ex)
            {
                await host.CloseAsync();
                ErrorOutput.Write(
                    [new TomixDiagnostic(
                        "TOMIX_UI_PORT_IN_USE",
                        DiagnosticSeverity.Error,
                        $"Cannot listen on port {parseResult.GetValue(portOption)}: {ex.Message}",
                        "Pick another --port, or leave it out to use a free one.")],
                    GlobalOptions.ErrorFormatValue(parseResult));
                return 2;
            }

            // From here the console belongs to the session's requests; the person reads these two.
            var stdout = Console.Out;
            var stderr = Console.Error;
            using var routing = ConsoleRouting.Install(log);
            await using (endpoint)
            {
                using var registration = _registry.Register(
                    new LiveEntry(session.Reference.Value, Environment.ProcessId, endpoint.Port, token, DateTimeOffset.UtcNow));
                var url = PageUrl(endpoint.BaseUrl, token);
                var grace = parseResult.GetValue(graceOption);
                Announce(stdout, json, url, endpoint.Port, session.Reference.Value, Environment.ProcessId, joined: false);
                if (!json)
                    stderr.WriteLine(
                        $"{session.Reference.Value} is open for the browser and agents. Ctrl+C stops; tx ui also stops {grace} s after the last client leaves.");

                using var lifetime = new UiLifetime(
                    () => host.ClientCount,
                    () => host.Session is { IsDirty: true } open ? open.Reference.Value : null,
                    TimeSpan.FromSeconds(grace),
                    stderr.WriteLine);
                host.ClientsChanged += (_, _) => lifetime.ClientsChanged();
                using var signals = ConsoleSignals.Install(signal =>
                {
                    if (signal == PosixSignal.SIGTERM)
                        lifetime.Stop();
                    else
                        lifetime.Interrupt();
                });
                try
                {
                    if (open)
                        Open(url, stderr);
                    await lifetime.Stopped;
                }
                finally
                {
                    await host.CloseAsync();
                }
            }

            return 0;
        });

        return command;
    }

    private static string PageUrl(Uri baseUrl, string token) => $"{baseUrl}?token={token}";

    /// <summary>The URL on stdout, alone or in the JSON envelope, so a script can read it.</summary>
    private static void Announce(TextWriter stdout, bool json, string url, int port, string model, int processId, bool joined)
    {
        if (!json)
        {
            stdout.WriteLine(url);
            return;
        }

        stdout.WriteLine(JsonOutput.Serialize(new CommandEnvelope<UiStarted>(
            new UiStarted(url, port, model, processId, joined), [])));
    }

    private void Open(string url, TextWriter stderr)
    {
        if (!_openBrowser(url))
            stderr.WriteLine("Could not open a browser: open the URL above.");
    }

    private static bool OpenBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>The <c>tx ui</c> JSON payload: where the session is served.</summary>
/// <param name="Url">The page, with the session token.</param>
/// <param name="Port">The port on <c>127.0.0.1</c>.</param>
/// <param name="Model">The open model.</param>
/// <param name="ProcessId">The process holding the session.</param>
/// <param name="Joined">Whether the session was already open in another <c>tx ui</c>.</param>
internal sealed record UiStarted(string Url, int Port, string Model, int ProcessId, bool Joined);
