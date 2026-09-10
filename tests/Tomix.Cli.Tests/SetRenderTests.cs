using Tomix.App.Set;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The set renderer previews DAX edits with a syntax-highlighted Before/After after the
/// <c>Set:</c> line. Non-DAX properties, unchanged values, and JSON output stay preview-free.
/// Asserted on true-color ANSI because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class SetRenderTests
{
    private const string Harbor = "\x1b[38;2;69;130;172m";  // functions
    private const string Sage = "\x1b[38;2;52;137;126m";    // table names
    private const string Moss = "\x1b[38;2;64;129;57m";     // column references

    [Fact]
    public void DaxEdit_ShowsColoredBeforeAndAfter()
    {
        var result = new SetModelPropertyResult(
            "Sales/Total Sales",
            "expression",
            "SUM('Sales'[Amount]) + 1",
            Saved: false,
            ValidationErrors: 0,
            OldValue: "SUM('Sales'[Amount])",
            IsDaxProperty: true);

        var output = Render(result);

        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Sage + "'Sales'", output);
        Assert.Contains(Moss + "[Amount]", output);
        Assert.Contains("Before: SUM('Sales'[Amount])", StripAnsi(output));
        Assert.Contains("After: SUM('Sales'[Amount]) + 1", StripAnsi(output));
    }

    [Fact]
    public void EmptyOldValue_RendersAnEmptyBeforeLine()
    {
        var result = new SetModelPropertyResult(
            "Sales/Total Sales",
            "expression",
            "SUM(Sales[Amount])",
            Saved: false,
            ValidationErrors: 0,
            OldValue: null,
            IsDaxProperty: true);

        var output = Render(result);

        Assert.Contains("Before: " + Environment.NewLine, StripAnsi(output));
        Assert.Contains(Harbor + "SUM", output);
    }

    [Fact]
    public void NonDaxProperty_ShowsNoPreview()
    {
        var result = new SetModelPropertyResult(
            "Sales/Month",
            "formatString",
            "\"$\"#,0",
            Saved: false,
            ValidationErrors: 0,
            OldValue: "\"#\"",
            IsDaxProperty: false);

        var output = Render(result);

        Assert.DoesNotContain(Harbor, output);
        Assert.DoesNotContain("Before:", StripAnsi(output));
        Assert.DoesNotContain("After:", StripAnsi(output));
    }

    [Fact]
    public void UnchangedDaxValue_ShowsNoPreview()
    {
        var result = new SetModelPropertyResult(
            "Sales/Total Sales",
            "expression",
            "SUM(Sales[Amount])",
            Saved: false,
            ValidationErrors: 0,
            OldValue: "SUM(Sales[Amount])",
            IsDaxProperty: true);

        var output = Render(result);

        Assert.DoesNotContain("Before:", StripAnsi(output));
        Assert.DoesNotContain("After:", StripAnsi(output));
    }

    private static string Render(SetModelPropertyResult result)
        => ConsoleCapture.Run(
            () =>
            {
                SetCommand.Render(result);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout;

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}
