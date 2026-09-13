using System.CommandLine;
using Spectre.Console;
using Tomix.App.Init;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class InitCommand : ICommandModule
{
    public Command Build()
    {
        var outputPathArgument = new Argument<string>("output-path")
        {
            Description = "Where to create the model; defaults to the global --model path",
            Arity = ArgumentArity.ZeroOrOne
        };

        var compatibilityLevelOption = new Option<int?>("--compatibility-level")
        {
            Description = "Compatibility level (default: 1702 when mode is PowerBI, 1500 otherwise)."
        };
        compatibilityLevelOption.Aliases.Add("--compat");

        var compatibilityModeOption = new Option<string?>("--compatibility-mode")
        {
            Description = "Compatibility mode: AnalysisServices, PowerBI. Default: PowerBI."
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Name of the new model (default: the directory's name)"
        };

        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "How the new model is written: tmdl, bim, or pbip (default: tmdl)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "pbip");

        var forceOption = new Option<bool>("--force")
        {
            Description = "Clear anything already at the target path"
        };

        var command = new Command("init", "Start a new empty semantic model")
        {
            outputPathArgument,
            compatibilityLevelOption,
            compatibilityModeOption,
            nameOption,
            serializationOption,
            forceOption
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "init", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var outputPath = parseResult.GetValue(outputPathArgument) ??
                             GlobalOptions.ModelValue(parseResult) ??
                             "";

            var result = new InitModelHandler().Handle(new InitModelRequest(
                outputPath,
                parseResult.GetValue(nameOption),
                parseResult.GetValue(serializationOption) ?? "",
                parseResult.GetValue(compatibilityModeOption) ?? "",
                parseResult.GetValue(compatibilityLevelOption),
                parseResult.GetValue(forceOption)));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    private static void Render(InitModelResult result)
    {
        AnsiConsole.MarkupLine(Styling.Success($"Created: {result.Created}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("  Name:   ", result.Name));
        AnsiConsole.MarkupLine(Styling.KeyValue("  Format: ", result.Format));
        AnsiConsole.MarkupLine(Styling.KeyValue("  Compat: ", $"{result.CompatibilityLevel} ({result.CompatibilityMode})"));
    }
}
