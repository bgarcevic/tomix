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
            Description = "The object to change, slash-separated. DAX form works too."
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var queryOption = new Option<string?>("-q")
        {
            Description = "Compatibility form of --set: the property to set; give its value with -i."
        };
        var valueOption = new Option<string?>("-i")
        {
            Description = "Value for the preceding -q. Pass '-' to read from stdin."
        };
        var setOption = new Option<string[]>("--set", "-p")
        {
            Description = "Property assignment as name=value. Repeat to set multiple properties together.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false
        };
        setOption.Validators.Add(result =>
        {
            foreach (var value in result.GetValueOrDefault<string[]>() ?? [])
                if (!value.Contains('=') || AddCommand.SplitSetName(value).Length == 0)
                    result.AddError($"--set '{value}' must be name=value.");
        });
        var forceOption = LifecycleOptions.Force();
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

        var command = new Command("set", "Change a property on a model object")
        {
            pathArgument,
            modelArgument,
            queryOption,
            valueOption,
            setOption,
            forceOption,
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
            var sets = parseResult.GetValue(setOption) ?? [];
            if (sets.Length > 0 && (!string.IsNullOrWhiteSpace(query) || rawValue is not null))
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

            IReadOnlyList<ModelPropertyAssignment> assignments = sets.Length > 0
                ? sets.Select(set => new ModelPropertyAssignment(
                    AddCommand.SplitSetName(set),
                    InputValueResolver.Resolve(set[(set.IndexOf('=') + 1)..]) ?? "")).ToArray()
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
                        DryRun: parseResult.GetValue(dryRunOption),
                        Force: parseResult.GetValue(forceOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    internal static void Render(SetModelPropertyResult result)
    {
        if (result.Status == MutationStatus.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success($"Reverted staged changes for {result.ObjectPath}."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Set: {result.ObjectPath}.{result.Property}"));

        // DAX edits get a Before/After preview so the change can be reviewed before saving.
        // Identical values skip it (the write still went through the lifecycle).
        if (result.IsDaxProperty
            && !string.Equals(result.OldValue, result.Value, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"{Styling.Bold("Before:")} {Styling.ExpressionMarkup(ExpressionLanguage.Dax, result.OldValue ?? "")}");
            AnsiConsole.MarkupLine($"{Styling.Bold("After:")} {Styling.ExpressionMarkup(ExpressionLanguage.Dax, result.Value ?? "")}");
        }

        MutationOutput.RenderPersistence(result.Outcome);

        if (result.CreatedExpressions is { Count: > 0 })
            AnsiConsole.MarkupLine(Styling.Guidance($"Created range parameters: {string.Join(", ", result.CreatedExpressions)}"));
        if (result.Policy is { } policy)
            foreach (var issue in policy.Issues)
                AnsiConsole.MarkupLine(issue.IsError
                    ? Styling.Error($"{issue.Code}: {issue.Message}")
                    : Styling.Warning($"{issue.Code}: {issue.Message}"));

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
