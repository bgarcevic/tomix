using System.CommandLine;
using System.Net.WebSockets;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Cli.Serve;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>tx serve</c>: holds a live session and answers the session protocol (docs/protocol.md) on
/// stdin and stdout, for editors, the tomix UI and agents (ADR 0001).
/// </summary>
internal sealed class ServeCommand : ICommandModule
{
    /// <summary>Streams a test drives the server through in place of stdin and stdout.</summary>
    internal static readonly AsyncLocal<(Stream Input, Stream Output)?> TestStreams = new();

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly CliStateStore _state;
    private readonly StagingStore _staging;
    private readonly string _version;
    private readonly Func<SessionScope?, IEnumerable<Command>, RootCommand> _buildSessionRoot;
    private readonly LiveRegistry _registry;

    /// <param name="buildSessionRoot">Builds the command tree the session's methods run, as for <c>tx interactive</c>.</param>
    /// <param name="registry">Where <c>tx ui</c> records live sessions; the user's by default.</param>
    public ServeCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        StagingStore staging,
        string version,
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildSessionRoot,
        LiveRegistry? registry = null)
    {
        _providers = providers;
        _state = state;
        _staging = staging;
        _version = version;
        _buildSessionRoot = buildSessionRoot;
        _registry = registry ?? LiveRegistry.Default;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Model to open at start: a TMDL folder or .bim file (default: none; the client sends session.open)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var logOption = new Option<string>("--log")
        {
            Description = "Append the server's log to this file instead of writing it to stderr",
            HelpName = "file"
        };

        var command = new Command("serve", "Serve a model session to editors and agents over stdio")
        {
            modelArgument,
            logOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // Ctrl+C or SIGTERM, or the caller's token: stop serving and close the session.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var stopCode = ConsoleSignals.InterruptExitCode;
            using var signals = ConsoleSignals.Install(signal =>
            {
                stopCode = ConsoleSignals.ExitCode(signal);
                stop.Cancel();
            });
            cancellationToken = stop.Token;

            var opener = new SessionOpener(_providers, _state, _staging);
            var logPath = parseResult.GetValue(logOption);
            await using var logFile = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
            var log = TextWriter.Synchronized(logFile ?? Console.Error);
            var streams = TestStreams.Value ?? (Console.OpenStandardInput(), Console.OpenStandardOutput());

            ILiveModelSession? session = null;
            if ((GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument)) is { Length: > 0 } model)
            {
                var reference = opener.Resolve(model, null, null);
                if (_registry.Find(reference.Value) is { } running)
                {
                    var joined = await JoinAsync(running, streams, log, parseResult, cancellationToken);
                    return stop.IsCancellationRequested ? stopCode : joined;
                }

                try
                {
                    (session, var openExit) = await opener.OpenAsync(parseResult, reference, cancellationToken);
                    if (session is null)
                        return openExit;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return stopCode;
                }
            }

            // stdout carries only frames: anything else written to the console goes to the log,
            // and nothing reads the console's stdin, which is the protocol's.
            using var routing = ConsoleRouting.Install(log);
            var host = new SessionHost(_buildSessionRoot, opener, log, session);
            try
            {
                var server = new ProtocolServer(streams.Input, streams.Output, host.Connect(), _version, log);
                log.WriteLine($"[tx serve] listening on stdio{(session is null ? "" : $" with {session.Reference.Value} open")}");
                var serving = server.RunAsync(cancellationToken);

                // A read on stdin may not cancel, so the server is not waited for once stopped. When
                // the read does cancel, the server ends as well: being stopped decides the exit code.
                var stopped = Task.Delay(Timeout.Infinite, cancellationToken);
                await Task.WhenAny(serving, stopped);
                if (!stop.IsCancellationRequested)
                    return await serving;
                log.WriteLine(stopCode == ConsoleSignals.InterruptExitCode ? "[tx serve] interrupted (Ctrl+C)" : "[tx serve] terminated");
                return stopCode;
            }
            finally
            {
                await host.CloseAsync();
            }
        });

        return command;
    }

    /// <summary>Relays to the session a running <c>tx ui</c> holds, so both work on one model.</summary>
    private static async Task<int> JoinAsync(
        LiveEntry running, (Stream Input, Stream Output) streams, TextWriter log, ParseResult parseResult, CancellationToken cancellationToken)
    {
        WebSocket socket;
        try
        {
            socket = await ServeRelay.ConnectAsync(running, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_UI_UNREACHABLE",
                    DiagnosticSeverity.Error,
                    $"{running.Model} is open in tx ui (process {running.ProcessId}), but its session cannot be reached: {ex.Message}",
                    "Stop that tx ui, or wait for it to start, then try again.")],
                GlobalOptions.ErrorFormatValue(parseResult));
            return 2;
        }

        using (socket)
        {
            log.WriteLine($"[tx serve] joining the session tx ui holds on {running.Model} (process {running.ProcessId})");
            return await ServeRelay.RunAsync(new StreamChannel(streams.Input, streams.Output), new WebSocketChannel(socket), cancellationToken);
        }
    }
}
