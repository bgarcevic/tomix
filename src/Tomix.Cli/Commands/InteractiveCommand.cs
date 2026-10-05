using System.CommandLine;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>tx interactive</c> (alias <c>tx shell</c>): opens a live session on one model and runs
/// commands against it until <c>exit</c> or the end of input (ADR 0001).
/// </summary>
internal sealed class InteractiveCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly CliStateStore _state;
    private readonly StagingStore _staging;
    private readonly Func<SessionScope?, IEnumerable<Command>, RootCommand> _buildSessionRoot;
    private readonly string _version;

    /// <param name="buildSessionRoot">Builds the command tree a session runs around the loop's own commands;
    /// with no model open, only those.</param>
    public InteractiveCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        StagingStore staging,
        string version,
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildSessionRoot)
    {
        _version = version;
        _providers = providers;
        _state = state;
        _staging = staging;
        _buildSessionRoot = buildSessionRoot;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Model to open: a TMDL folder or .bim file; defaults to the active connection, else none",
            Arity = ArgumentArity.ZeroOrOne
        };
        var autosaveOption = new Option<bool>("--autosave")
        {
            Description = "Save after every command that changes the model, instead of only when you run 'save'"
        };
        var discardOption = new Option<bool>("--discard-on-exit")
        {
            Description = "Leave without asking, discarding unsaved changes (so does --yes)"
        };
        var echoOption = new Option<bool>("--echo")
        {
            Description = "Print each command before it runs, so a log of a scripted session reads in order"
        };
        var noBatchOption = new Option<bool>("--no-batch")
        {
            Description = "Keep running a script after a command fails (by default piped input stops at the first failure)"
        };
        var noBannerOption = new Option<bool>("--no-banner")
        {
            Description = "Start without the welcome lines"
        };

        var command = new Command("interactive", "Edit a model in a session with undo, then save")
        {
            modelArgument,
            autosaveOption,
            discardOption,
            echoOption,
            noBatchOption,
            noBannerOption
        };
        command.Aliases.Add("shell");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, format);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "interactive", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var opener = new SessionOpener(_providers, _state, _staging);
            var explicitModel = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            if (!opener.TryResolve(parseResult, explicitModel, out var reference, out var isImplicit, out var resolveExit))
                return resolveExit;

            // A model the command names must open; the active connection is only a default, so a
            // session starts without a model when there is none or it cannot be opened.
            ILiveModelSession? session = null;
            if (!isImplicit || !string.IsNullOrWhiteSpace(reference.Value))
            {
                (session, var openExit) = await opener.OpenAsync(parseResult, reference, cancellationToken);
                if (session is null && !isImplicit)
                    return openExit;
            }

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var testInput = InputValueResolver.TestStdin.Value;
            var terminal = testInput is null && !Console.IsInputRedirected;
            var canPrompt = terminal && InteractionGate.CanPrompt(parseResult, format);
            var oneShot = parseResult.RootCommandResult.Command.Subcommands
                .SelectMany(sub => sub.Aliases.Prepend(sub.Name))
                .ToHashSet(StringComparer.Ordinal);
            await using var loop = new InteractiveLoop(
                _buildSessionRoot,
                opener,
                oneShot,
                new InteractiveOptions(
                    Autosave: parseResult.GetValue(autosaveOption),
                    DiscardOnExit: parseResult.GetValue(discardOption) || parseResult.GetValue(GlobalOptions.Yes),
                    Echo: parseResult.GetValue(echoOption),
                    Batch: !parseResult.GetValue(noBatchOption),
                    Banner: terminal && !quiet && !parseResult.GetValue(noBannerOption) && !OutputFormats.IsJson(format),
                    CanPrompt: canPrompt,
                    errorFormat,
                    InheritedOptions(parseResult)),
                _version,
                session);

            ILineReader reader = terminal ? new ConsoleLineEditor(loop.Complete) : new RedirectedLineReader(testInput ?? Console.In);
            return await loop.RunAsync(reader, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// The global options given to <c>tx interactive</c> itself, as tokens every line inherits:
    /// <c>tx interactive --output-format json</c> makes JSON each command's default.
    /// </summary>
    internal static IReadOnlyList<(Option Option, string[] Tokens)> InheritedOptions(ParseResult parseResult)
    {
        var inherited = new List<(Option, string[])>();
        foreach (var option in new Option[] { GlobalOptions.OutputFormat, GlobalOptions.ErrorFormat, GlobalOptions.Auth })
            if (parseResult.GetResult(option) is { Implicit: false } result && result.Tokens.Count > 0)
                inherited.Add((option, [option.Name, result.Tokens[0].Value]));
        foreach (var flag in new[] { GlobalOptions.Quiet, GlobalOptions.Debug, GlobalOptions.Yes, GlobalOptions.NonInteractive })
            if (parseResult.GetValue(flag))
                inherited.Add((flag, [flag.Name]));
        return inherited;
    }
}
