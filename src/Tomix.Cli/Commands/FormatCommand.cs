using System.CommandLine;
using Spectre.Console;
using Tomix.App.Format;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class FormatCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IExpressionFormatterClient _formatter;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public FormatCommand(
        IReadOnlyList<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        CliStateStore state,
        MutationStores mutations)
    {
        _providers = providers;
        _formatter = formatter;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var expressionOption = new Option<string?>("--expression")
        {
            Description = "Format an inline expression"
        };
        expressionOption.Aliases.Add("-e");
        var pathOption = new Option<string?>("--path")
        {
            Description = "Format the expression on a model object path"
        };
        var longOption = new Option<bool>("--long")
        {
            Description = "Prefer long lines when formatting M"
        };
        var saveToOption = LifecycleOptions.SaveTo();
        var langOption = new Option<string?>("--lang")
        {
            Description = "Expression language: dax or m"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate object type"
        };
        typeOption.Aliases.Add("-t");
        var saveOption = LifecycleOptions.Save();
        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();
        var dryRunOption = LifecycleOptions.DryRun();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();

        var command = new Command("format", "Format DAX or M/Power Query expressions (--expression inline, --path object path, or all)")
        {
            modelArgument,
            expressionOption,
            pathOption,
            longOption,
            saveToOption,
            langOption,
            typeOption,
            saveOption,
            forceOption,
            overwriteOption,
            dryRunOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "format", OutputFormats.Text, OutputFormats.Json))
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

            var expression = InputValueResolver.Resolve(parseResult.GetValue(expressionOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Formatting...",
                () => new FormatModelHandler(_providers, _formatter, _mutations).HandleAsync(
                    new FormatModelRequest(
                        model,
                        expression,
                        parseResult.GetValue(pathOption),
                        parseResult.GetValue(langOption) ?? "",
                        type,
                        parseResult.GetValue(longOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        Serialization: "",
                        Force: parseResult.GetValue(forceOption),
                        Overwrite: parseResult.GetValue(overwriteOption),
                        Stage: parseResult.GetValue(stageOption),
                        Revert: parseResult.GetValue(revertOption),
                        NoSync: parseResult.GetValue(noSyncOption),
                        DryRun: parseResult.GetValue(dryRunOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                Render,
                data => (object)data,
                renderCsv: null,
                GlobalOptions.ErrorFormatValue(parseResult, formatValue));
        });

        return command;
    }

    // A sweep failure must not be silent: otherwise an HTTP error from the M formatter reports
    // only "Failed: N" and the user has to re-run inline to learn why. A uniform failure prints
    // once with the affected-object count; distinct failures print once per object.
    private static void WriteFailureDetails(IReadOnlyList<ModelFormatObjectResult> results)
    {
        var failures = results
            .Where(r => r.Status == "failed" && !string.IsNullOrWhiteSpace(r.Error))
            .ToList();

        foreach (var group in failures.GroupBy(r => r.Error))
        {
            var first = group.First();
            var label = first.Measure is null
                ? $"{first.Table}/{first.Partition}"
                : $"{first.Table}/{first.Measure}";
            var more = group.Count() - 1;
            var suffix = more > 0 ? $" (+{more} more)" : "";
            Console.Error.WriteLine($"{label}{suffix}: {group.Key}");
        }
    }

    private static void Render(FormatModelResult result)
    {
        switch (result)
        {
            case InlineFormatResult inline:
                AnsiConsole.WriteLine(inline.Formatted);
                foreach (var error in inline.Errors)
                    Console.Error.WriteLine(error);
                break;

            case ObjectFormatResult obj:
                AnsiConsole.WriteLine(obj.Formatted);
                if (obj.DryRun == true)
                    AnsiConsole.MarkupLine(Styling.Guidance("Dry run: nothing was saved."));
                if (obj.Synced)
                    AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(obj.SyncTarget!)}"));
                else if (obj.SyncWarning is not null)
                    AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(obj.SyncWarning)));
                break;

            case ModelFormatResult model:
                AnsiConsole.MarkupLine(Styling.Success($"Formatted: {model.Formatted}"));
                AnsiConsole.MarkupLine(Styling.Warning($"Unchanged: {model.Unchanged}"));
                AnsiConsole.MarkupLine(Styling.Error($"Failed: {model.Failed}"));
                WriteFailureDetails(model.Results);

                if (model.Saved is true or string)
                    AnsiConsole.MarkupLine(Styling.Success("Model saved."));
                else if (model.Staged == true)
                    AnsiConsole.MarkupLine(Styling.Success("Mutation staged."));
                else if (model.DryRun == true)
                    AnsiConsole.MarkupLine(Styling.Guidance("Dry run: nothing was saved."));
                else if (model.Formatted > 0)
                    AnsiConsole.MarkupLine(Styling.Muted("Not saved — re-run with --save to persist or --stage to stage."));

                if (model.Synced)
                    AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(model.SyncTarget!)}"));
                else if (model.SyncWarning is not null)
                    AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(model.SyncWarning)));
                break;
        }
    }
}
