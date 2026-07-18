using System.CommandLine;
using Spectre.Console;
using Tomix.App.Replace;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class ReplaceCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ReplaceCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var patternArgument = new Argument<string>("pattern")
        {
            Description = "Text or regex pattern to search for",
            Arity = ArgumentArity.ZeroOrOne
        };
        var replacementArgument = new Argument<string>("replacement")
        {
            Description = "Replacement text",
            Arity = ArgumentArity.ZeroOrOne
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var inOption = new Option<string?>("--in")
        {
            Description = "Scope: names, expressions, descriptions, displayFolders, formatStrings, annotations, all. 'all' covers every scope except annotations (explicit-only: values are often tool-generated JSON)."
        };
        var regexOption = new Option<bool>("--regex")
        {
            Description = "Treat pattern as a regular expression"
        };
        var caseSensitiveOption = new Option<bool>("--case-sensitive")
        {
            Description = "Enable case-sensitive matching"
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview changes without applying"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Save even if the replacement introduces validation errors"
        };
        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };
        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location"
        };
        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save to a different path (implies --save)"
        };
        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");

        var command = new Command("replace", "Find and replace text across model objects")
        {
            patternArgument,
            replacementArgument,
            modelArgument,
            inOption,
            regexOption,
            caseSensitiveOption,
            dryRunOption,
            forceOption,
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
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun && !ConfirmationHelper.ConfirmOrAbort(
                "Replace", $"'{pattern}'",
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;
            var reference = RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Replacing...",
                () => new ReplaceModelTextHandler(_providers).HandleAsync(
                    new ReplaceModelTextRequest(
                        reference,
                        pattern,
                        parseResult.GetValue(replacementArgument) ?? "",
                        parseResult.GetValue(inOption) ?? "all",
                        parseResult.GetValue(regexOption),
                        parseResult.GetValue(caseSensitiveOption),
                        dryRun,
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, Render);
        });

        return command;
    }

    private static void Render(ReplaceModelTextResult result)
    {
        AnsiConsole.MarkupLine(Styling.Value($"Changes: {result.ChangeCount}"));
        if (result.DryRun is true)
        {
            foreach (var preview in result.Previews ?? [])
                AnsiConsole.WriteLine($"{preview.ObjectPath}.{preview.Property}: {preview.Before} -> {preview.After}");
            return;
        }

        AnsiConsole.MarkupLine(result.Saved is false ? Styling.Warning("Saved: false") : Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }
}
