using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Rules;

namespace Tomix.Cli.Tests;

/// <summary>
/// The validate banner prints the model name carried on the result — never a hardcoded
/// literal — and escapes it, so a name containing markup brackets renders literally instead
/// of being parsed as Spectre markup. The issue table highlights each offending expression
/// line below its message.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class ValidateRenderTests
{
    private const string Function = "\x1b[38;5;12m";  // functions (bright blue)
    private const string Reference = "\x1b[38;5;6m"; // measure references (cyan)
    private const string Dim = "\x1b[2m";        // muted gutter

    [Fact]
    public void Render_Banner_ShowsResultModelName()
    {
        var result = new ValidateModelResult(ModelName: "basic-tmdl", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        // The banner is commentary (#255): stderr, never the findings on stdout.
        Assert.Contains("Validating: basic-tmdl", captured.Stderr);
        Assert.DoesNotContain("Validating", captured.Stdout);
        Assert.DoesNotContain("(unnamed)", captured.Stderr);
    }

    [Fact]
    public void Render_EscapesMarkupBrackets_InModelName()
    {
        var result = new ValidateModelResult(ModelName: "Sales [Q1]", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains("Validating: Sales [Q1]", captured.Stderr);
    }

    [Fact]
    public void Render_HighlightsOffendingExpressionLine()
    {
        var result = ResultWithExpression("SUM('Sales'[Missing])");

        var output = Render(result, noMultiline: false);

        Assert.Contains(Function + "SUM", output);
        Assert.Contains("('Sales'[Missing])", output);
        Assert.Contains(Dim + "│ ", output);
    }

    [Fact]
    public void Render_ResolvesMeasureReferences_WithMeasureNames()
    {
        var result = new ValidateModelResult(
            ModelName: "basic-tmdl",
            Valid: false,
            DurationMs: 1,
            Errors:
            [
                new ValidationIssue(
                    RuleSeverity.Warning,
                    "DAX0003", "Measure or column [Profit] cannot be found in the model.",
                    "Sales/Profit %", "1", "DIVIDE([Profit], 'Sales'[Qty])")
            ],
            Warnings: [],
            MeasureNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit" });

        var output = Render(result, noMultiline: false);

        Assert.Contains(Reference + "[Profit]", output);
        Assert.Contains(", 'Sales'[Qty])", output);
    }

    [Fact]
    public void Render_NoMultiline_SuppressesExpressionLine()
    {
        var result = ResultWithExpression("SUM('Sales'[Missing])");

        var output = Render(result, noMultiline: true);

        Assert.DoesNotContain(Function, output);
        Assert.DoesNotContain("SUM('Sales'[Missing])", output);
    }

    [Theory]
    [InlineData(false, "[Group]")]
    [InlineData(true, "[​Group]")]
    public void Render_CiLog_KeepsFindingsUnwrapped_AndEscapesAzureCommands(bool azureLog, string expectedReference)
    {
        var message = "Column [Group] cannot be found on table 'Column Axis (Forecast With A Long Table Name)'.";
        var result = new ValidateModelResult(
            ModelName: "basic-tmdl",
            Valid: false,
            DurationMs: 1,
            Errors:
            [
                new ValidationIssue(
                    RuleSeverity.Error, "DAX0002", message,
                    "Column Axis (Forecast With A Long Table Name)/Forecast Column Value", "29",
                    "VAR Grp = SELECTEDVALUE ( 'Column Axis (Forecast With A Long Table Name)'[Group] )")
            ],
            Warnings: []);

        var output = StripAnsi(ConsoleCapture.Run(
            () =>
            {
                ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: false, ciLog: true, azureLog);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout);

        var lines = output.Split('\n');
        Assert.Contains(lines, line =>
            line.Contains(message.Replace("[Group]", expectedReference)) && line.Contains("/Forecast Column Value"));
        Assert.Contains(lines, line => line.Contains("'Column Axis (Forecast With A Long Table Name)'" + expectedReference));
    }

    [Fact]
    public void Render_StructuralIssue_WithoutExpressionLine_StaysPlain()
    {
        var result = new ValidateModelResult(
            ModelName: "basic-tmdl",
            Valid: false,
            DurationMs: 1,
            Errors:
            [
                new ValidationIssue(
                    RuleSeverity.Error,
                    "TOMIX_BROKEN_SORT_BY",
                    "Sort-by column 'MonthNo' cannot be found on table 'Sales'.",
                    "Sales/Month",
                    "1")
            ],
            Warnings: []);

        var output = Render(result, noMultiline: false);

        Assert.DoesNotContain(Function, output);
        Assert.DoesNotContain(Dim + "│ ", output);
        Assert.Contains("Sort-by column 'MonthNo'", StripAnsi(output));
    }

    private static ValidateModelResult ResultWithExpression(string expressionLine) => new(
        ModelName: "basic-tmdl",
        Valid: false,
        DurationMs: 1,
        Errors:
        [
            new ValidationIssue(
                RuleSeverity.Error,
                "DAX0002",
                "Column [Missing] cannot be found on table 'Sales'.",
                "Sales/Total Sales",
                "1",
                expressionLine)
        ],
        Warnings: []);

    private static string Render(ValidateModelResult result, bool noMultiline)
        => ConsoleCapture.Run(
            () =>
            {
                ValidateRenderer.Render(result, errorsOnly: false, noMultiline, includeBanner: false);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout;

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}
