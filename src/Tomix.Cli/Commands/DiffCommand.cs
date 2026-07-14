using System.CommandLine;
using Tomix.App.Diff;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class DiffCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public DiffCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var leftArgument = new Argument<string>("left")
        {
            Description = "Path to first model (TMDL folder, .bim file)"
        };

        var rightArgument = new Argument<string>("right")
        {
            Description = "Path to second model (TMDL folder, .bim file)"
        };

        var command = new Command(
            "diff",
            "Compare two semantic models and show structural differences. Exit codes: 0 = identical, 1 = differences found, 2 = error")
        {
            leftArgument,
            rightArgument
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(formatValue, "diff", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var left = parseResult.GetValue(leftArgument) ?? "";
            var right = parseResult.GetValue(rightArgument) ?? "";
            var result = await CliSpinner.RunAsync(
                "Comparing models...",
                () => new DiffModelHandler(_providers).HandleAsync(
                    new DiffModelRequest(
                        new ModelReference(left),
                        new ModelReference(right)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                errorFormat,
                data => Render(data, left, right, includeComparingLine: !OutputFormats.IsCsv(formatValue)));
        });

        return command;
    }

    private static void Render(
        DiffModelResult result,
        string left,
        string right,
        bool includeComparingLine)
    {
        if (includeComparingLine)
            AnsiConsole.MarkupLine(Styling.Value("Comparing models..."));

        if (!result.HasChanges)
        {
            AnsiConsole.MarkupLine(Styling.Success("Models are identical"));
            return;
        }

        AnsiConsole.MarkupLine(Styling.KeyValue("Left:", left));
        AnsiConsole.MarkupLine(Styling.KeyValue("Right:", right));
        AnsiConsole.MarkupLine(
            Styling.Bold($"{result.Summary.Added} added, {result.Summary.Removed} removed, {result.Summary.Modified} modified"));
        AnsiConsole.WriteLine();

        foreach (var change in result.Changes)
            RenderChange(change);
    }

    private static void RenderChange(DiffChange change)
    {
        switch (change.Action)
        {
            case "added":
                AnsiConsole.MarkupLine($"  {Styling.Success("+")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "removed":
                AnsiConsole.MarkupLine($"  {Styling.Error("-")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "modified":
                AnsiConsole.MarkupLine($"  {Styling.Warning("~")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                AnsiConsole.MarkupLine($"    {Styling.Error($"- {change.OldValue}")}");
                AnsiConsole.MarkupLine($"    {Styling.Success($"+ {change.NewValue}")}");
                break;
        }
    }
}
