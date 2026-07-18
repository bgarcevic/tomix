using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Rm;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class RmCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public RmCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path to remove."
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Force removal even if object has dependents"
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be removed without saving"
        };
        var ifExistsOption = new Option<bool>("--if-exists")
        {
            Description = "Succeed silently if the object does not exist"
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
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple table-children."
        };
        typeOption.Aliases.Add("-t");
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location"
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

        var command = new Command("rm", "Remove an object from the model")
        {
            pathArgument,
            modelArgument,
            forceOption,
            dryRunOption,
            ifExistsOption,
            saveToOption,
            serializationOption,
            typeOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "rm", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var typeValue = parseResult.GetValue(typeOption);
            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError();
                }

                type = parsed;
            }

            var path = parseResult.GetValue(pathArgument) ?? "";
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun && !ConfirmationHelper.ConfirmOrAbort(
                "Remove", path,
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _services.State,
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
                () => new RemoveModelObjectHandler(_providers, _services.Mutations).HandleAsync(
                    new RemoveModelObjectRequest(
                        reference,
                        path,
                        type,
                        parseResult.GetValue(ifExistsOption),
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

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), Render);
        });

        return command;
    }

    private static void Render(RemoveModelObjectResult result)
    {
        if (result.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success("Reverted."));
            return;
        }

        if (result.Removed is false)
        {
            // The only Changed=false path is --if-exists on a missing object; say so instead of
            // exiting silently.
            if (result.Reason == "not_found" && result.Path is not null)
                AnsiConsole.MarkupLine(Styling.Success(
                    $"Not found: {Styling.MarkupEscape(result.Path)} (nothing removed)"));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Removed: {result.Removed}"));
        if (result.CascadeRemoved is { Count: > 0 } cascade)
            foreach (var item in cascade)
                AnsiConsole.MarkupLine(Styling.Muted($"Also removed: {item}"));

        if (result.BrokenReferences is { Count: > 0 } broken)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Warning: {broken.Count} DAX reference(s) to the removed object are now broken: "
                + $"{string.Join(", ", broken)}. Update them with 'tx replace' or inspect with 'tx deps'."));

        if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist."));
        else if (result.Saved is not null)
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }
}
