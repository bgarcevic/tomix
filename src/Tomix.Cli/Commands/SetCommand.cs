using System.CommandLine;
using Tomix.App.Set;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class SetCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public SetCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

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
            Description = "Property expression."
        };
        var valueOption = new Option<string?>("-i")
        {
            Description = "Value for the preceding -q. Use '-' to read from stdin."
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Allow --save-to to overwrite an existing target"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple objects (e.g. a measure and a partition sharing a name)."
        };
        typeOption.Aliases.Add("-t");
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

        var command = new Command("set", "Set a property on a model object")
        {
            pathArgument,
            modelArgument,
            queryOption,
            valueOption,
            forceOption,
            typeOption,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(formatValue))
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

            var query = parseResult.GetValue(queryOption);
            var value = InputValueResolver.Resolve(parseResult.GetValue(valueOption));
            IReadOnlyList<ModelPropertyAssignment> assignments = string.IsNullOrWhiteSpace(query)
                ? Array.Empty<ModelPropertyAssignment>()
                : [new ModelPropertyAssignment(query, value ?? "")];
            var reference = new ActiveModelResolver().ResolveReference(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Database),
                parseResult.GetValue(GlobalOptions.Server));
            var label = MutationSpinnerLabel.For(
                parseResult.GetValue(saveOption),
                parseResult.GetValue(saveToOption),
                parseResult.GetValue(stageOption),
                parseResult.GetValue(revertOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                label,
                () => new SetModelPropertyHandler(_providers).HandleAsync(
                    new SetModelPropertyRequest(
                        reference,
                        parseResult.GetValue(pathArgument) ?? "",
                        assignments,
                        type,
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
        else if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist or --stage to stage."));
        else
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }
}
