using System.CommandLine;
using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Replace;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class ReplaceCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public ReplaceCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, MutationStores mutations)
    {
        _providers = providers;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var patternArgument = new Argument<string>("pattern")
        {
            Description = "The text or regex to look for",
            Arity = ArgumentArity.ZeroOrOne
        };
        var replacementArgument = new Argument<string>("replacement")
        {
            Description = "Replacement text",
            Arity = ArgumentArity.ZeroOrOne
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var inOption = new Option<string?>("--in")
        {
            Description = "Where to look: names, expressions, descriptions, displayFolders, formatStrings, annotations, or all " +
                          "(default: all, which skips annotations; name them explicitly to include them)"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = $"Only replace in objects of this kind, {TypeValidation.KindsHint}"
        };
        typeOption.Aliases.Add("-t");
        var regexOption = new Option<bool>("--regex")
        {
            Description = "Interpret the pattern as a regular expression"
        };
        var caseSensitiveOption = new Option<bool>("--case-sensitive")
        {
            Description = "Match text exactly, including letter case"
        };
        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();
        var saveOption = LifecycleOptions.Save();
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();

        var command = new Command("replace", "Replace text across model objects")
        {
            patternArgument,
            replacementArgument,
            modelArgument,
            inOption,
            typeOption,
            regexOption,
            caseSensitiveOption,
            forceOption,
            overwriteOption,
            stageOption,
            revertOption,
            noSyncOption,
            saveOption,
            saveToOption,
            serializationOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "replace", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var pattern = parseResult.GetValue(patternArgument) ?? "";

            ModelObjectKind? type = null;
            var typeValue = parseResult.GetValue(typeOption);
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                    return TypeValidation.WriteInvalidTypeError(errorFormat);

                type = parsed;
            }

            // Without --save/--save-to/--stage the replace is only previewed, so it asks nothing.
            var persists = parseResult.GetValue(revertOption)
                || LifecycleOptions.Persists(
                    parseResult.GetValue(saveOption), parseResult.GetValue(saveToOption), parseResult.GetValue(stageOption));
            if (persists && !ConfirmationHelper.ConfirmOrAbort(
                "Replace", $"'{pattern}'", parseResult, formatValue))
                return 1;

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var reference,
                    out var recentExit))
                return recentExit;
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Replacing...",
                () => new ReplaceModelTextHandler(_providers, _mutations).HandleAsync(
                    new ReplaceModelTextRequest(
                        reference,
                        pattern,
                        parseResult.GetValue(replacementArgument) ?? "",
                        parseResult.GetValue(inOption) ?? "all",
                        parseResult.GetValue(regexOption),
                        parseResult.GetValue(caseSensitiveOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption),
                        type,
                        Overwrite: parseResult.GetValue(overwriteOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    private static void Render(ReplaceModelTextResult result)
    {
        AnsiConsole.MarkupLine(Styling.Value($"Changes: {result.ChangeCount}"));
        if (result.Status == MutationStatus.Preview)
            foreach (var preview in result.Previews ?? [])
                AnsiConsole.WriteLine($"{preview.ObjectPath}.{preview.Property}: {preview.Before} -> {preview.After}");

        MutationOutput.RenderPersistence(result.Outcome);
    }
}
