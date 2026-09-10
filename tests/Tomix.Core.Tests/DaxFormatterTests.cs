using Tomix.Core.Dax;
using Tomix.Core.Dax.Engine;

namespace Tomix.Core.Tests;

/// <summary>
/// Contract tests for the offline formatter facade. Formatting must never change the code: the
/// token stream (strings and literals included) and the comment sequence of the output must
/// match the input's, with whitespace, line breaks, and the upper-casing of keywords and known
/// function names as the only permitted differences.
/// </summary>
public sealed class DaxFormatterTests
{
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    private static readonly string LongExpression =
        "SUMX(VALUES('Product'[Category]), CALCULATE(SUM(Sales[Amount]) + SUM(Sales[Cost]) * 1.5, "
        + "KEEPFILTERS('Product'[Color] = \"Red\" || 'Product'[Color] = \"Blue\"), "
        + "TREATAS({\"A\", \"B\", \"C\"}, 'Region'[Name])))";

    [Fact]
    public void Format_ShortExpression_StaysOnOneLine()
        => Assert.Equal(
            "CALCULATE ( SUM ( sales[amt] ) )",
            DaxFormatter.Format("CALCULATE(sum(sales[amt]))"));

    [Fact]
    public void Format_UppercasesKeywordsAndFunctions_ButNotIdentifiers()
    {
        var formatted = DaxFormatter.Format("sum(sales[amt])");

        Assert.Equal("SUM ( sales[amt] )", formatted);
    }

    [Fact]
    public void Format_LongExpression_ExpandsToMultipleLines()
        => Assert.Equal(
            Lines(
                "SUMX (",
                "    VALUES ( 'Product'[Category] ),",
                "    CALCULATE (",
                "        SUM ( Sales[Amount] ) + SUM ( Sales[Cost] ) * 1.5,",
                "        KEEPFILTERS (",
                "            'Product'[Color] = \"Red\" || 'Product'[Color] = \"Blue\"",
                "        ),",
                "        TREATAS ( { \"A\", \"B\", \"C\" }, 'Region'[Name] )",
                "    )",
                ")"),
            DaxFormatter.Format(LongExpression));

    [Theory]
    [InlineData("SUM(Sales[Amount]) + [Total]")]
    [InlineData("CALCULATE(sum(sales[amt]))")]
    [InlineData("VAR total = SUM(Sales[Amount]) VAR count = COUNTROWS(Sales) RETURN DIVIDE(total, count, 0)")]
    [InlineData("SUM(Sales[Amount]) -- trailing")]
    [InlineData("/* leading */ SUM(Sales[Amount])")]
    [InlineData("SUM(Sales[Amount]) /* trailing block */ + 1")]
    [InlineData("[Weird ]] Name] & \"a\"\"b\"")]
    [InlineData("TOPN(1, Sales, Sales[Amount], , [Price])")]
    [InlineData("dt\"2024-01-01\"")]
    [InlineData("COUNTROWS('It''s')")]
    [InlineData("IF(x > 1, {1, 2}, {3})")]
    [InlineData("VALUES('Product'[Category])")]
    public void Format_PreservesTokensAndComments_IsIdempotent(string dax)
    {
        var once = DaxFormatter.Format(dax);

        Assert.True(DaxFormatter.TryFormat(dax, out _), $"Formatting failed for: {dax}");
        Assert.Equal(
            DaxCodeFormatter.Signature(dax),
            DaxCodeFormatter.Signature(once),
            StringComparer.Ordinal);
        Assert.Equal(
            DaxCodeFormatter.CommentSignature(dax),
            DaxCodeFormatter.CommentSignature(once),
            StringComparer.Ordinal);
        Assert.Equal(once, DaxFormatter.Format(once));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \n\t ")]
    public void TryFormat_NoDax_ReturnsFalseAndOriginal(string dax)
    {
        var formatted = "sentinel";

        Assert.False(DaxFormatter.TryFormat(dax, out formatted));
        Assert.Equal(dax, formatted);
    }

    [Fact]
    public void TryFormat_MultilineBlockCommentWouldBeReindented_ReturnsFalseWithOriginal()
    {
        // The printer re-indents a block comment that ends up on an indented line, which shifts
        // the comment's continuation lines. That would alter the comment text, so formatting
        // declines and the input comes back unchanged.
        var dax = "SUM(Sales[Amount]) -- total sales\n/* block\n   comment */\n+ [Cost]";

        Assert.False(DaxFormatter.TryFormat(dax, out var formatted));
        Assert.Equal(Lines(
            "SUM(Sales[Amount]) -- total sales",
            "/* block",
            "   comment */",
            "+ [Cost]"), formatted);
    }

    [Fact]
    public void TryFormat_WhenFormattingDeclines_ReportsFirstDifferenceLine()
    {
        // Printing moves the trailing line comment after the block comment (reordering comments
        // changes their sequence), so the result must point at the first affected comment.
        var dax = "SUM(Sales[Amount]) -- total sales\n/* block\n   comment */\n+ [Cost]";

        var result = DaxFormatter.TryFormat(dax);

        Assert.False(result.Success);
        Assert.Equal(Lines(
            "SUM(Sales[Amount]) -- total sales",
            "/* block",
            "   comment */",
            "+ [Cost]"), result.Formatted);
        Assert.Equal(1, result.FirstDifferenceLine);
    }

    [Fact]
    public void TryFormat_FencedDaxBlock_UnwrapsAndFormats()
    {
        var fenced = "```dax\nCALCULATE(sum(sales[amt]))\n```";

        Assert.True(DaxFormatter.TryFormat(fenced, out var formatted));
        Assert.Equal("CALCULATE ( SUM ( sales[amt] ) )", formatted);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(501)]
    public void Format_LineLengthOutsideSupportedRange_Throws(int maximumLineLength)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => DaxFormatter.Format("SUM(Sales[Amount])", maximumLineLength));
}
