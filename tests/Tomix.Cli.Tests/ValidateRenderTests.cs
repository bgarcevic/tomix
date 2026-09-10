using Tomix.App.Validate;
using Tomix.Cli.Output;

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
    private const string Harbor = "\x1b[38;2;69;130;172m";  // functions
    private const string Sage = "\x1b[38;2;52;137;126m";    // table names
    private const string Moss = "\x1b[38;2;64;129;57m";     // column references
    private const string Orchid = "\x1b[38;2;207;103;172m"; // measure references
    private const string Slate = "\x1b[38;2;117;127;136m";  // muted gutter

    [Fact]
    public void Render_Banner_ShowsResultModelName()
    {
        var result = new ValidateModelResult(ModelName: "basic-tmdl", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains("Validating: basic-tmdl", captured.Stdout);
        Assert.DoesNotContain("(unnamed)", captured.Stdout);
    }

    [Fact]
    public void Render_EscapesMarkupBrackets_InModelName()
    {
        var result = new ValidateModelResult(ModelName: "Sales [Q1]", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains("Validating: Sales [Q1]", captured.Stdout);
    }

    [Fact]
    public void Render_HighlightsOffendingExpressionLine()
    {
        var result = ResultWithExpression("SUM('Sales'[Missing])");

        var output = Render(result, noMultiline: false);

        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Sage + "'Sales'", output);
        Assert.Contains(Slate + "│ ", output);
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
                    "DAX0003", "Measure or column [Profit] cannot be found in the model.",
                    "Sales/Profit %", "1", "DIVIDE([Profit], 'Sales'[Qty])")
            ],
            Warnings: [],
            MeasureNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit" });

        var output = Render(result, noMultiline: false);

        Assert.Contains(Orchid + "[Profit]", output);
        Assert.Contains(Moss + "[Qty]", output);
    }

    [Fact]
    public void Render_NoMultiline_SuppressesExpressionLine()
    {
        var result = ResultWithExpression("SUM('Sales'[Missing])");

        var output = Render(result, noMultiline: true);

        Assert.DoesNotContain(Harbor, output);
        Assert.DoesNotContain(Sage, output);
        Assert.DoesNotContain("SUM('Sales'[Missing])", output);
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
                    "TOMIX_BROKEN_SORT_BY",
                    "Sort-by column 'MonthNo' cannot be found on table 'Sales'.",
                    "Sales/Month",
                    "1")
            ],
            Warnings: []);

        var output = Render(result, noMultiline: false);

        Assert.DoesNotContain(Harbor, output);
        Assert.DoesNotContain(Sage, output);
        Assert.DoesNotContain(Slate + "│ ", output);
        Assert.Contains("Sort-by column 'MonthNo'", StripAnsi(output));
    }

    private static ValidateModelResult ResultWithExpression(string expressionLine) => new(
        ModelName: "basic-tmdl",
        Valid: false,
        DurationMs: 1,
        Errors:
        [
            new ValidationIssue(
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
