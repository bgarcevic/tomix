using System.CommandLine;
using System.Text.Json;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Tests;

/// <summary>
/// M that does not lex or parse fails <c>tx format</c> with <c>TOMIX_FORMAT_FAILED</c> and says
/// where (#197): JSON errors carry structured <c>syntaxErrors</c>, and text errors for an inline
/// expression show the offending line with a caret. Runs the real offline M engine.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatSyntaxErrorTests
{
    private const string StrayComma = "let x = 1, in x";

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new FormatCommand(
            [], new OfflineMFormatterClient(), services.State, services.Mutations).Build());
    }

    [Fact]
    public void InlineSyntaxError_Json_CarriesStructuredSyntaxErrors()
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["format", "-e", StrayComma, "--lang", "m", "--output-format", "json"]));

        Assert.Equal(1, captured.ExitCode);
        Assert.Equal("", captured.Stdout);
        using var json = JsonDocument.Parse(captured.Stderr);
        var root = json.RootElement;
        Assert.Equal("TOMIX_FORMAT_FAILED", root.GetProperty("code").GetString());
        Assert.Equal(1, root.GetProperty("line").GetInt32());
        Assert.Equal(12, root.GetProperty("column").GetInt32());

        var error = Assert.Single(root.GetProperty("syntaxErrors").EnumerateArray().ToList());
        Assert.Equal("parse", error.GetProperty("stage").GetString());
        Assert.Equal("expectedCsvContinuation", error.GetProperty("code").GetString());
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        Assert.Equal(12, error.GetProperty("column").GetInt32());
        Assert.Equal(1, error.GetProperty("endLine").GetInt32());
        Assert.Equal(13, error.GetProperty("endColumn").GetInt32());
    }

    [Fact]
    public void InlineSyntaxError_Text_PointsAtTheOffendingToken()
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["format", "-e", StrayComma, "--lang", "m"]),
            captureAnsiConsole: true);

        Assert.Equal(1, captured.ExitCode);
        var lines = StripAnsi(captured.Stderr).ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("1 | let x = 1, in x", lines);
        Assert.Contains("  |            ^^", lines);
    }

    [Fact]
    public void InlineSyntaxError_Quiet_OmitsTheCaret()
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["format", "-e", StrayComma, "--lang", "m", "--quiet"]),
            captureAnsiConsole: true);

        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("line 1, column 12", captured.Stderr);
        Assert.DoesNotContain("^", captured.Stderr);
    }

    [Theory]
    // A token span on the start line: one caret per character.
    [InlineData("let x = 1, in x", 1, 12, 1, 13, "1", "let x = 1, in x", "           ^^")]
    // Only a start (lex errors): a single caret.
    [InlineData("let x = \"abc in x", 1, 9, null, null, "1", "let x = \"abc in x", "        ^")]
    // Later lines: the gutter shows the line, and tabs in the prefix are kept so the caret lines up.
    [InlineData("let\n\tx = 1,\nin\n\tx", 3, 1, 3, 2, "3", "in", "^^")]
    [InlineData("let\n\tx = [a = ]\nin\n\tx", 2, 11, 2, 11, "2", "\tx = [a = ]", "\t         ^")]
    // A span that ends on a later line: mark the start only.
    [InlineData("{1,\n2", 1, 1, 2, 1, "1", "{1,", "^")]
    public void Caret_Render(
        string source, int line, int column, int? endLine, int? endColumn,
        string gutter, string sourceLine, string caret)
    {
        var rendered = SyntaxErrorCaret.Render(
            source, new ExpressionSyntaxError("parse", null, "m", line, column, endLine, endColumn));

        Assert.Equal((gutter, sourceLine, caret), rendered);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(9, 1)] // Beyond the source.
    public void Caret_WithoutAUsablePosition_RendersNothing(int? line, int? column)
        => Assert.Null(SyntaxErrorCaret.Render(
            "let x = 1 in x", new ExpressionSyntaxError("parse", null, "m", line, column)));

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}
