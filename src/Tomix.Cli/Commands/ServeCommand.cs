using System.CommandLine;
using Spectre.Console;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Cli.Serve;
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

    /// <param name="buildSessionRoot">Builds the command tree the session's methods run, as for <c>tx interactive</c>.</param>
    public ServeCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        StagingStore staging,
        string version,
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildSessionRoot)
    {
        _providers = providers;
        _state = state;
        _staging = staging;
        _version = version;
        _buildSessionRoot = buildSessionRoot;
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
            var opener = new SessionOpener(_providers, _state, _staging);
            ILiveModelSession? session = null;
            if ((GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument)) is { Length: > 0 } model)
            {
                (session, var openExit) = await opener.OpenAsync(parseResult, opener.Resolve(model, null, null), cancellationToken);
                if (session is null)
                    return openExit;
            }

            var logPath = parseResult.GetValue(logOption);
            await using var logFile = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
            var log = TextWriter.Synchronized(logFile ?? Console.Error);
            var streams = TestStreams.Value ?? (Console.OpenStandardInput(), Console.OpenStandardOutput());

            // stdout carries only frames: anything else written to the console goes to the log,
            // and nothing reads the console's stdin, which is the protocol's.
            var originalOut = Console.Out;
            var originalIn = Console.In;
            var originalAnsi = AnsiConsole.Console;
            Console.SetOut(log);
            Console.SetIn(TextReader.Null);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(log),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors
            });
            try
            {
                var methods = new ServeSession(_buildSessionRoot, opener, log, session);
                var server = new ProtocolServer(streams.Input, streams.Output, methods, _version, log);
                log.WriteLine($"[tx serve] listening on stdio{(session is null ? "" : $" with {session.Reference.Value} open")}");
                return await server.RunAsync(cancellationToken);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetIn(originalIn);
                AnsiConsole.Console = originalAnsi;
            }
        });

        return command;
    }
}
