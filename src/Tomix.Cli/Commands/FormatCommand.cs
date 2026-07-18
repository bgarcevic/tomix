using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Format;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class FormatCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IExpressionFormatterClient _formatter;

    private readonly AppServices _services;

    public FormatCommand(
        IReadOnlyList<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        AppServices services)
    {
        _providers = providers;
        _formatter = formatter;
        _services = services;
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
        pathOption.Aliases.Add("-p");
        var semicolonsOption = new Option<bool>("--semicolons")
        {
            Description = "Use semicolons as DAX list separators"
        };
        var longOption = new Option<bool>("--long")
        {
            Description = "Prefer long-line formatting"
        };
        var noSpaceAfterFunctionOption = new Option<bool>("--no-space-after-function")
        {
            Description = "Do not insert a space between a DAX function name and '('"
        };
        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save formatted model to a different path"
        };
        var langOption = new Option<string?>("--lang")
        {
            Description = "Expression language: dax or m"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate object type"
        };
        typeOption.Aliases.Add("-t");
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist formatted expressions to the source model"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Save even if this mutation introduces validation errors"
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

        var command = new Command("format", "Format DAX or M/Power Query expressions (-e inline, -p object path, or all)")
        {
            modelArgument,
            expressionOption,
            pathOption,
            semicolonsOption,
            longOption,
            noSpaceAfterFunctionOption,
            saveToOption,
            langOption,
            typeOption,
            saveOption,
            forceOption,
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
                    return TypeValidation.WriteInvalidTypeError();
                }

                type = parsed;
            }

            var expression = InputValueResolver.Resolve(parseResult.GetValue(expressionOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _services.State,
                    out var model,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Formatting...",
                () => new FormatModelHandler(_providers, _formatter, _services.Mutations).HandleAsync(
                    new FormatModelRequest(
                        model,
                        expression,
                        parseResult.GetValue(pathOption),
                        parseResult.GetValue(langOption) ?? "",
                        type,
                        parseResult.GetValue(longOption),
                        parseResult.GetValue(semicolonsOption),
                        parseResult.GetValue(noSpaceAfterFunctionOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                Render,
                data => (object)data);
        });

        return command;
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
                if (obj.Synced)
                    AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(obj.SyncTarget!)}"));
                else if (obj.SyncWarning is not null)
                    AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(obj.SyncWarning)));
                break;

            case ModelFormatResult model:
                AnsiConsole.MarkupLine(Styling.Success($"Formatted: {model.Formatted}"));
                AnsiConsole.MarkupLine(Styling.Warning($"Unchanged: {model.Unchanged}"));
                AnsiConsole.MarkupLine(Styling.Error($"Failed: {model.Failed}"));

                if (model.Saved is true or string)
                    AnsiConsole.MarkupLine(Styling.Success("Model saved."));
                else if (model.Staged == true)
                    AnsiConsole.MarkupLine(Styling.Success("Mutation staged."));
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
