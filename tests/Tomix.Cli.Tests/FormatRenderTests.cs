using Tomix.App.Format;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The format renderer syntax-highlights DAX in text output (inline <c>-e</c> and
/// <c>--path</c>), while M stays plain and a piped/redirected write comes back as the
/// formatted expression alone — format output is often copy-pasted back into a model.
/// Asserted on true-color ANSI because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatRenderTests
{
    private const string Harbor = "\x1b[38;2;69;130;172m";  // functions
    private const string Sage = "\x1b[38;2;52;137;126m";    // table names
    private const string Moss = "\x1b[38;2;64;129;57m";     // column references

    [Fact]
    public void InlineDax_IsHighlighted()
    {
        var output = Render(new InlineFormatResult(
            true, "CALCULATE(SUM('Sales'[Amount]))", "dax", []));

        Assert.Contains(Harbor + "CALCULATE", output);
        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Sage + "'Sales'", output);
        Assert.Contains(Moss + "[Amount]", output);
    }

    [Fact]
    public void InlinePowerQuery_StaysPlain()
    {
        var output = Render(new InlineFormatResult(
            true, "let Source = 1 in Source", "m", []));

        Assert.DoesNotContain(Harbor, output);
        Assert.Contains("let Source = 1 in Source", StripAnsi(output));
    }

    [Fact]
    public void InlineDax_StrippedOutput_IsTheFormattedExpression()
    {
        const string formatted = "CALCULATE(SUM('Sales'[Amount]), 'Sales'[Region] = \"West\")";

        var output = Render(new InlineFormatResult(true, formatted, "dax", []));

        Assert.Equal(formatted, StripAnsi(output).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void ObjectPathDax_IsHighlighted()
    {
        var output = Render(new ObjectFormatResult(
            true, "Sales/Total Sales", "dax", "formatted", "SUM(Sales[Amount])", Saved: null));

        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Moss + "[Amount]", output);
    }

    [Fact]
    public void ObjectPathM_StaysPlain()
    {
        var output = Render(new ObjectFormatResult(
            true, "Sales/Sales", "m", "formatted", "let Source = 1 in Source", Saved: null));

        Assert.DoesNotContain(Harbor, output);
        Assert.Contains("let Source = 1 in Source", StripAnsi(output));
    }

    private static string Render(FormatModelResult result)
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
