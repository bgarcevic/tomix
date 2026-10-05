using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using Tomix.App.Models;
using Tomix.App.Session;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Interactive;

/// <param name="Autosave">Save after every command that leaves the session with unsaved changes.</param>
/// <param name="DiscardOnExit">Leave without asking, discarding unsaved changes.</param>
/// <param name="Echo">Print each line before it runs.</param>
/// <param name="Batch">Stop at the first failing command. Applies only when input is not a terminal.</param>
/// <param name="Banner">Show the welcome screen. Applies only at a terminal.</param>
/// <param name="CanPrompt">Whether a person is at the keyboard to answer prompts.</param>
/// <param name="ErrorFormat">The session's <c>--error-format</c>, for its own messages.</param>
/// <param name="Inherited">Global options given to the session, added to every line that does not set them.</param>
internal sealed record InteractiveOptions(
    bool Autosave,
    bool DiscardOnExit,
    bool Echo,
    bool Batch,
    bool Banner,
    bool CanPrompt,
    string? ErrorFormat,
    IReadOnlyList<(Option Option, string[] Tokens)> Inherited);

/// <summary>
/// The read-run loop of <c>tx interactive</c>. Each line is parsed through the open model's
/// command tree and runs as one transaction on its live session; the session-only commands
/// (<see cref="SessionCommands"/>) act on the session itself. The loop can start with no model
/// and owns whichever session <c>open</c> opened last.
/// </summary>
internal sealed class InteractiveLoop : IAsyncDisposable
{
    /// <summary>The client name the session's change events and history report.</summary>
    public const string Client = "shell";

    private readonly Func<SessionScope?, IEnumerable<Command>, RootCommand> _buildRoot;
    private readonly SessionOpener _opener;
    private readonly IReadOnlySet<string> _oneShotCommands;
    private readonly InteractiveOptions _options;
    private readonly string _version;
    private SessionScope? _scope;
    private LiveSessionHandler? _handler;
    private RootCommand _root;
    private CancellationTokenSource? _running;
    private bool _exitRequested;

    /// <param name="buildRoot">Builds the command tree around the loop's own commands: with a
    /// session, the commands that run on its model; without one, only the loop's.</param>
    /// <param name="oneShotCommands">Every top-level command of <c>tx</c>, to explain the ones a session lacks.</param>
    /// <param name="session">The model opened at start, if any; the loop owns it from here.</param>
    public InteractiveLoop(
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildRoot,
        SessionOpener opener,
        IReadOnlySet<string> oneShotCommands,
        InteractiveOptions options,
        string version,
        ILiveModelSession? session)
    {
        _buildRoot = buildRoot;
        _opener = opener;
        _oneShotCommands = oneShotCommands;
        _options = options;
        _version = version;
        _root = Attach(session);
    }

    public RootCommand Root => _root;

    /// <summary>Completions for a line typed up to the cursor, for the line editor's Tab.</summary>
    public IReadOnlyList<string> Complete(string typed)
    {
        try
        {
            return _root.Parse(typed).GetCompletions(typed.Length).Select(item => item.InsertText ?? item.Label).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A completion source that fails (a model that cannot be read) offers nothing.
            return [];
        }
    }

    /// <summary>Runs until <c>exit</c> or the end of input, and returns the process exit code.</summary>
    public async Task<int> RunAsync(ILineReader reader, CancellationToken cancellationToken)
    {
        if (_options.Banner)
            InteractiveBanner.Write(StdErr.Console(), _version, await ModelInfoAsync(cancellationToken));

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            var firstFailure = 0;
            var lineNumber = 0;
            while (true)
            {
                var line = reader.ReadLine(Prompt());
                lineNumber++;
                if (line is null)
                {
                    if (await TryExitAsync(cancellationToken) is { } code)
                        return code != 0 ? code : firstFailure;
                    continue;
                }

                line = line.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (_options.Echo)
                    StdErr.MarkupLine(Styling.Muted($"> {line}"));

                var exitCode = await ExecuteAsync(line, cancellationToken);
                if (_exitRequested)
                {
                    _exitRequested = false;
                    if (await TryExitAsync(cancellationToken) is { } code)
                        return code != 0 ? code : firstFailure;
                    continue;
                }

                if (exitCode != 0)
                {
                    if (_options.CanPrompt)
                        continue;

                    if (firstFailure == 0)
                        firstFailure = exitCode;
                    if (_options.Batch)
                    {
                        await StopAsync(lineNumber, cancellationToken);
                        return exitCode;
                    }

                    continue;
                }

                if (_options.Autosave && _scope is { Session.IsDirty: true } && _handler is { InTransaction: false })
                {
                    var saved = await ExecuteAsync("save", cancellationToken);
                    if (saved != 0 && !_options.CanPrompt && _options.Batch)
                    {
                        await StopAsync(lineNumber, cancellationToken);
                        return saved;
                    }
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    /// <summary>Parses and runs one line. A line naming a command the session lacks is explained
    /// rather than reported as unknown.</summary>
    internal async Task<int> ExecuteAsync(string line, CancellationToken cancellationToken)
    {
        var args = Program.RewriteHelpCommand(CommandLineParser.SplitCommandLine(line).ToArray());
        if (args.Length > 0
            && (_oneShotCommands.Contains(args[0]) || SessionCommands.Names.Contains(args[0]))
            && !_root.Subcommands.Any(command => command.Name == args[0] || command.Aliases.Contains(args[0])))
        {
            if (_scope is null)
                Error(
                    "TOMIX_SESSION_NO_MODEL",
                    $"No model is open for '{args[0]}'.",
                    $"Run 'connect <path>' first, or 'tx {args[0]}' outside the session.");
            else
                Error(
                    "TOMIX_SESSION_COMMAND_UNAVAILABLE",
                    $"'{args[0]}' is not available inside an interactive session.",
                    $"Run 'tx {args[0]}' outside the session; 'help' lists what works here.");
            return 2;
        }

        args = Inherit(args);
        var parseResult = _root.Parse(args);
        if (parseResult.Errors.Count > 0)
            return UsageErrors.Report(parseResult, args);
        if (UnknownOptionGuard.TryReject(parseResult, args))
            return 2;

        // 'connect' changes the active connection, and the session follows it. Whether the open
        // model may be closed is settled first, so a refusal leaves the connection alone too.
        var connecting = Connects(parseResult);
        if (connecting && await TryLeaveAsync("connect to another model", cancellationToken) is not 0)
            return 1;
        var active = connecting ? _opener.ActiveReference() : null;

        // Each line is one transaction; its label is what history shows.
        if (_scope is not null)
            _scope.Source.Options = new LiveLeaseOptions(Client, line);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        try
        {
            var exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null },
                running.Token);
            if (connecting && exitCode == 0 && _opener.ActiveReference() is { Value.Length: > 0 } connected && connected != active)
                return await SwitchAsync(parseResult, connected, running.Token);
            return exitCode;
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            StdErr.MarkupLine(Styling.Warning("Cancelled."));
            return 130;
        }
        catch (Exception ex)
        {
            return Program.ReportFailure(ex, parseResult);
        }
        finally
        {
            _running = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is not null)
            await _scope.Session.DisposeAsync();
    }

    /// <summary>Makes <paramref name="session"/> the open model and builds its command tree.</summary>
    private RootCommand Attach(ILiveModelSession? session)
    {
        _scope = session is null ? null : new SessionScope(new LiveSessionSource(session, new LiveLeaseOptions(Client)));
        _handler = session is null ? null : new LiveSessionHandler(session, Client);
        IEnumerable<Command> commands = [ExitCommand()];
        if (_handler is not null)
            commands = commands.Concat(SessionCommands.Build(_handler));
        _root = _buildRoot(_scope, commands);
        return _root;
    }

    private Command ExitCommand()
    {
        var command = new Command("exit", "Leave the session; asks first when there are unsaved changes");
        command.Aliases.Add("quit");
        command.SetAction(_ =>
        {
            _exitRequested = true;
            return 0;
        });
        return command;
    }

    /// <summary>Whether this is a <c>connect</c> that sets a connection, rather than one that shows,
    /// lists, clears or asks for help.</summary>
    private static bool Connects(ParseResult parseResult)
        => parseResult.CommandResult.Command.Name == "connect"
           && parseResult.Tokens.Count > 1
           && !parseResult.Tokens.Any(token => token.Value is "--list" or "--clear" or "--help" or "-h" or "-?");

    /// <summary>Opens the model <c>connect</c> made active in place of the open one. A model that
    /// fails to load leaves the open one and its work alone.</summary>
    private async Task<int> SwitchAsync(ParseResult parseResult, ModelReference reference, CancellationToken cancellationToken)
    {
        var (session, exitCode) = await _opener.OpenAsync(parseResult, reference, cancellationToken);
        if (session is null)
        {
            if (_scope is not null)
                StdErr.MarkupLine(Styling.Guidance($"The session still edits {ModelName(_scope.Model)}."));
            return exitCode;
        }

        await DiscardAsync(cancellationToken);
        if (_scope is not null)
            await _scope.Session.DisposeAsync();
        Attach(session);
        StdErr.MarkupLine(Styling.Guidance($"The session now edits {ModelName(reference)}."));
        return 0;
    }

    private async Task<SessionModelInfo?> ModelInfoAsync(CancellationToken cancellationToken)
        => _scope is null ? null
            : new SessionModelInfo(_scope.Model.Value, _scope.Session.SourcePath, await _scope.Session.GetSummaryAsync(cancellationToken));

    /// <summary>The line's arguments plus each inherited option the line does not set itself.</summary>
    private string[] Inherit(string[] args)
    {
        var inherited = _options.Inherited
            .Where(entry => !args.Any(arg => Names(entry.Option).Any(name =>
                arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal) || arg.StartsWith(name + ":", StringComparison.Ordinal))))
            .SelectMany(entry => entry.Tokens);
        return [.. args, .. inherited];
    }

    private static IEnumerable<string> Names(Option option) => option.Aliases.Prepend(option.Name);

    /// <summary>
    /// Whether the open model may be closed for <paramref name="action"/>: yes when nothing would
    /// be lost, when <c>--discard-on-exit</c> or <c>--yes</c> says so, or when the person at the
    /// keyboard confirms. Returns 0 to go ahead, 1 after reporting a refusal, or <c>null</c> when
    /// the person declined.
    /// </summary>
    private int? TryLeave(string action)
    {
        if (_handler is not { HasUnsavedWork: true } || _options.DiscardOnExit)
            return 0;

        var what = Unsaved();
        if (!_options.CanPrompt)
        {
            Error(
                "TOMIX_SESSION_DIRTY",
                $"The session has {what}; '{action}' would discard them.",
                $"Run 'save' first, or pass --discard-on-exit (or --yes) to {action} without saving.");
            return 1;
        }

        return StdErr.Console().Confirm($"  Discard {Styling.MarkupEscape(what)} and {Styling.MarkupEscape(action)}?", defaultValue: false)
            ? 0
            : null;
    }

    private Task<int?> TryLeaveAsync(string action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryLeave(action));
    }

    /// <summary><c>exit</c> or the end of input: leaves when <see cref="TryLeave"/> allows.</summary>
    private async Task<int?> TryExitAsync(CancellationToken cancellationToken)
    {
        var leave = TryLeave("exit");
        if (leave == 0)
            await DiscardAsync(cancellationToken);
        return leave;
    }

    /// <summary>Drops the open model's unsaved work, saying what was lost.</summary>
    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        if (_handler is not { HasUnsavedWork: true })
            return;

        var what = Unsaved();
        await _handler.RollbackOpenTransactionAsync(cancellationToken);
        StdErr.MarkupLine(Styling.Warning($"Discarded {what}."));
    }

    /// <summary>Batch mode: a command failed, so the rest of the script does not run.</summary>
    private async Task StopAsync(int lineNumber, CancellationToken cancellationToken)
    {
        var lost = _handler is { HasUnsavedWork: true } ? $"; discarded {Unsaved()}" : "";
        if (_handler is not null)
            await _handler.RollbackOpenTransactionAsync(cancellationToken);
        StdErr.MarkupLine(Styling.Warning($"Stopped at line {lineNumber}{lost}. Pass --no-batch to run past failures."));
    }

    private string Unsaved()
        => _handler is { InTransaction: true }
            ? _scope!.Session.IsDirty ? "unsaved changes and an open transaction" : "an open transaction"
            : "unsaved changes";

    private void Error(string code, string message, string hint)
        => ErrorOutput.Write([new TomixDiagnostic(code, DiagnosticSeverity.Error, message, hint)], _options.ErrorFormat);

    /// <summary><c>tx [model*] (transaction)&gt;</c>, or <c>tx&gt;</c> with no model open.</summary>
    private string Prompt()
    {
        var tx = Styling.Title("tx");
        if (_scope is null)
            return $"{tx}{Styling.Muted(">")} ";

        var dirty = _scope.Session.IsDirty ? Styling.Warning("*") : "";
        var transaction = _handler is { InTransaction: true } ? " " + Styling.Muted("(transaction)") : "";
        return $"{tx} {Styling.Muted("[")}{Styling.Path(ModelName(_scope.Model))}{dirty}{Styling.Muted("]")}{transaction}{Styling.Muted(">")} ";
    }

    /// <summary>The short name the prompt shows: the folder or file name, or the remote database.</summary>
    internal static string ModelName(ModelReference model)
    {
        if (model.IsRemote && !string.IsNullOrWhiteSpace(model.Database))
            return model.Database;

        var path = Path.TrimEndingDirectorySeparator(model.Value);
        var name = Path.GetFileName(path);
        if (Path.HasExtension(name) && File.Exists(path))
            name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? name : model.Value;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Ctrl-C stops the running command, never the session.
        e.Cancel = true;
        _running?.Cancel();
    }
}
