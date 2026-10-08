using Tomix.App.Format;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The format renderer syntax-highlights DAX and M in text output (inline <c>-e</c> and
/// <c>--path</c>), and a piped/redirected write comes back as the formatted expression alone — format output is often copy-pasted back into a model.
/// Asserted on ANSI escapes because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatRenderTests
{
    private const string Function = "\x1b[38;5;12m"; // functions (bright blue)
    private const string Keyword = "\x1b[38;5;5m";  // keywords (magenta)

    [Fact]
    public void InlineDax_IsHighlighted()
    {
        var output = Render(new InlineFormatResult(
            true, "CALCULATE(SUM('Sales'[Amount]))", "dax"));

        Assert.Contains(Function + "CALCULATE", output);
        Assert.Contains(Function + "SUM", output);
        // Tables and columns stay plain.
        Assert.Contains("('Sales'[Amount])", output);
    }

    [Fact]
    public void InlinePowerQuery_IsHighlighted()
    {
        var output = Render(new InlineFormatResult(
            true, "let\n    Source = Table.FromRows({})\nin\n    Source", "m"));

        Assert.Contains(Keyword + "let", output);
        Assert.Contains("    Source = ", output);
        Assert.Contains(Function + "Table.FromRows", output);
    }

    [Fact]
    public void InlinePowerQuery_StrippedOutput_IsTheFormattedExpression()
    {
        var formatted = """
            let
                #"Typed" = Table.TransformColumnTypes(Source, {{"A", type number}}),
                Filtered = Table.SelectRows(#"Typed", each [A] > 0 and [#"Is Open"])
                // [red]not markup[/]
            in
                Filtered
            """.ReplaceLineEndings("\n");

        var output = Render(new InlineFormatResult(true, formatted, "m"));

        Assert.Equal(formatted, StripAnsi(output).TrimEnd('\r', '\n').ReplaceLineEndings("\n"));
    }

    [Fact]
    public void InlineDax_StrippedOutput_IsTheFormattedExpression()
    {
        const string formatted = "CALCULATE(SUM('Sales'[Amount]), 'Sales'[Region] = \"West\")";

        var output = Render(new InlineFormatResult(true, formatted, "dax"));

        Assert.Equal(formatted, StripAnsi(output).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void ObjectPathDax_IsHighlighted()
    {
        var output = Render(new ObjectFormatResult(
            true, "Sales/Total Sales", "dax", "formatted", "SUM(Sales[Amount])"));

        Assert.Contains(Function + "SUM", output);
        Assert.Contains("(Sales[Amount])", output);
    }

    [Fact]
    public void ObjectPathM_IsHighlighted()
    {
        var output = Render(new ObjectFormatResult(
            true, "Sales/Sales", "m", "formatted", "let Source = 1 in Source"));

        Assert.Contains(Keyword + "let", output);
        Assert.Contains(" Source = ", output);
        Assert.Contains("let Source = 1 in Source", StripAnsi(output));
    }

    [Theory]
    [InlineData(1, "Formatted: 2 (not applied)", false)]
    [InlineData(0, "Formatted: 2", true)]
    public void WholeModel_FailureMarksFormattedCountNotApplied(int failed, string summary, bool saveHint)
    {
        // One failure applies nothing, so "Formatted: 2" alone would claim changes that were not made.
        var output = StripAnsi(Render(new ModelFormatResult(2 + failed, 2, 0, failed, [])));

        Assert.Contains(summary, output.Split('\n').Select(line => line.TrimEnd()));
        Assert.Equal(saveHint, output.Contains("re-run with --save"));
    }

    private static string Render(IFormatModelResult result)
        => ConsoleCapture.Run(
            () =>
            {
                FormatCommand.Render(result);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout;

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}
