using System.CommandLine;
using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Set;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class SetCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public SetCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, MutationStores mutations)
    {
        _providers = providers;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path. Slash-separated paths and DAX forms are accepted."
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var queryOption = new Option<string?>("-q")
        {
            Description = "Compatibility form of --set: the property to set; give its value with -i."
        };
        var valueOption = new Option<string?>("-i")
        {
            Description = "Value for the preceding -q. Use '-' to read from stdin."
        };
        var setOption = new Option<string?>("--set")
        {
            Description = "Property assignment as name=value, e.g. --set expression=\"SUM(Sales[Amount])\". Names accept dotted paths, bracket indexers, and DisplayName matching."
        };
        setOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is not null && AddCommand.SplitSetName(value).Length == 0)
                result.AddError($"--set '{value}' must be name=value.");
        });
        var overwriteOption = LifecycleOptions.Overwrite();
        var dryRunOption = LifecycleOptions.DryRun();
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple objects (e.g. a measure and a partition sharing a name)."
        };
        typeOption.Aliases.Add("-t");
        var saveOption = LifecycleOptions.Save();
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();
        var strictRefsOption = new Option<bool>("--strict-refs")
        {
            Description = "Fail when a rename leaves DAX references broken (with fixup on, only unfixable references fail)."
        };
        var noFixRefsOption = new Option<bool>("--no-fix-refs")
        {
            Description = "Do not rewrite DAX references to the renamed object; warn instead."
        };

        var command = new Command("set", "Set a property on a model object")
        {
            pathArgument,
            modelArgument,
            queryOption,
            valueOption,
            setOption,
            overwriteOption,
            dryRunOption,
            typeOption,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption,
            strictRefsOption,
            noFixRefsOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var typeValue = parseResult.GetValue(typeOption);
            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError(GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                }

                type = parsed;
            }

            var query = parseResult.GetValue(queryOption);
            var rawValue = parseResult.GetValue(valueOption);
            var set = parseResult.GetValue(setOption);
            if (set is not null && (!string.IsNullOrWhiteSpace(query) || rawValue is not null))
            {
                ErrorOutput.Write(
                    [new TomixDiagnostic(
                        "TOMIX_SET_INPUT_CONFLICT",
                        DiagnosticSeverity.Error,
                        "Pass either --set or -q/-i, not both.",
                        "Prefer --set name=value; -q/-i remain as the compatibility form.")],
                    GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                return 2;
            }

            IReadOnlyList<ModelPropertyAssignment> assignments = set is not null
                ? [new ModelPropertyAssignment(
                    AddCommand.SplitSetName(set),
                    InputValueResolver.Resolve(set[(set.IndexOf('=') + 1)..]) ?? "")]
                : string.IsNullOrWhiteSpace(query)
                    ? Array.Empty<ModelPropertyAssignment>()
                    : [new ModelPropertyAssignment(query, InputValueResolver.Resolve(rawValue) ?? "")];
            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var reference,
                    out var recentExit))
                return recentExit;
            var label = MutationSpinnerLabel.For(
                parseResult.GetValue(saveOption),
                parseResult.GetValue(saveToOption),
                parseResult.GetValue(stageOption),
                parseResult.GetValue(revertOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                label,
                () => new SetModelPropertyHandler(_providers, _mutations).HandleAsync(
                    new SetModelPropertyRequest(
                        reference,
                        parseResult.GetValue(pathArgument) ?? "",
                        assignments,
                        type,
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption),
                        parseResult.GetValue(strictRefsOption),
                        FixRefs: !parseResult.GetValue(noFixRefsOption),
                        Overwrite: parseResult.GetValue(overwriteOption),
                        DryRun: parseResult.GetValue(dryRunOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    private static void Render(SetModelPropertyResult result)
    {
        if (string.IsNullOrEmpty(result.Property))
        {
            AnsiConsole.MarkupLine(Styling.Success($"Reverted staged changes for {result.Set}."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Set: {result.Set}.{result.Property}"));
        if (result.Staged == true)
            AnsiConsole.MarkupLine(Styling.Guidance("Staged. Run 'tx stage commit' to promote."));
        else if (result.DryRun == true)
            AnsiConsole.MarkupLine(Styling.Guidance("Dry run: nothing was saved."));
        else if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist or --stage to stage."));
        else
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));

        RenderFixedReferences(result.FixedReferences);
        RenderBrokenReferences(result.BrokenReferences);
    }

    internal static void RenderFixedReferences(IReadOnlyList<string>? references)
    {
        if (references is not { Count: > 0 })
            return;

        AnsiConsole.MarkupLine(Styling.Success(Styling.MarkupEscape(
            $"Updated {references.Count} DAX reference(s) in: {string.Join(", ", references)}")));
    }

    internal static void RenderBrokenReferences(IReadOnlyList<string>? references)
    {
        if (references is not { Count: > 0 })
            return;

        AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(
            $"Warning: {references.Count} DAX reference(s) to the old name are now broken: {string.Join(", ", references)}. "
            + "Update them with 'tx replace' or inspect with 'tx deps'.")));
    }
}
