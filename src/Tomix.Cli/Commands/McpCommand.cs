using System.CommandLine;
using System.Net.WebSockets;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Serve;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>tx mcp</c>: a Model Context Protocol server on stdin and stdout (#354), so an agent harness
/// (Claude Code, Codex, Cursor and others) works on a model through typed tools. It is one more
/// client of a live session: of the one <c>tx ui</c> holds for the model, so the person watches
/// and undoes the agent's edits there, or of one it holds itself.
/// </summary>
internal sealed class McpCommand : ICommandModule
{
    /// <summary>Streams a test drives the server through in place of stdin and stdout.</summary>
    internal static readonly AsyncLocal<(Stream Input, Stream Output)?> TestStreams = new();

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly CliStateStore _state;
    private readonly StagingStore _staging;
    private readonly string _version;
    private readonly Func<SessionScope?, IEnumerable<Command>, RootCommand> _buildSessionRoot;
    private readonly LiveRegistry _registry;
    private readonly Func<LiveEntry, CancellationToken, Task<WebSocket>>? _connect;

    /// <param name="buildSessionRoot">Builds the command tree the session's methods run, as for <c>tx serve</c>.</param>
    /// <param name="registry">Where <c>tx ui</c> records live sessions; the user's by default.</param>
    /// <param name="connect">Connects to a <c>tx ui</c>'s session; over its WebSocket by default.</param>
    public McpCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        StagingStore staging,
        string version,
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildSessionRoot,
        LiveRegistry? registry = null,
        Func<LiveEntry, CancellationToken, Task<WebSocket>>? connect = null)
    {
        _providers = providers;
        _state = state;
        _staging = staging;
        _version = version;
        _buildSessionRoot = buildSessionRoot;
        _registry = registry ?? LiveRegistry.Default;
        _connect = connect;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Model to open at start: a TMDL folder or .bim file (default: none; the agent calls session_open)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var readOnlyOption = new Option<bool>("--read-only")
        {
            Description = "List only the tools that change nothing: no edits, undo or save"
        };
        var logOption = new Option<string>("--log")
        {
            Description = "Append the server's log to this file instead of writing it to stderr",
            HelpName = "file"
        };

        var command = new Command("mcp", "Serve a model session to AI agents as MCP tools over stdio")
        {
            modelArgument,
            readOnlyOption,
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
            LiveEntry? running = null;
            if ((GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument)) is { Length: > 0 } model)
            {
                var reference = opener.Resolve(model, null, null);
                running = _registry.Find(reference.Value);
                if (running is null)
                {
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
            }

            // stdout carries only MCP messages: anything else written to the console goes to the
            // log, and nothing reads the console's stdin, which is the client's.
            using var routing = ConsoleRouting.Install(log);
            var mcp = new McpSession(new SessionHost(_buildSessionRoot, opener, log, session), _registry, _connect, running);
            try
            {
                var tools = McpTools.Build((RootCommand)parseResult.RootCommandResult.Command, parseResult.GetValue(readOnlyOption));
                using var channel = new LineChannel(streams.Input, streams.Output);
                var server = new McpServer(channel, mcp, tools, _version, log);
                log.WriteLine($"[tx mcp] listening on stdio{(running is not null ? $", joining tx ui on {running.Model}" : session is null ? "" : $" with {session.Reference.Value} open")}");
                var serving = server.RunAsync(cancellationToken);

                // A read on stdin may not cancel, so the server is not waited for once stopped.
                var stopped = Task.Delay(Timeout.Infinite, cancellationToken);
                await Task.WhenAny(serving, stopped);
                if (!stop.IsCancellationRequested)
                {
                    await serving;
                    return 0;
                }

                log.WriteLine(stopCode == ConsoleSignals.InterruptExitCode ? "[tx mcp] interrupted (Ctrl+C)" : "[tx mcp] terminated");
                return stopCode;
            }
            finally
            {
                await mcp.DisposeAsync();
            }
        });

        return command;
    }
}
