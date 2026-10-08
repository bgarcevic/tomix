using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxLexicalTokensTests
{
    [Theory]
    [InlineData("'It''s'", DaxLexemeKind.QuotedTable, "It's")]
    [InlineData("[Col]]umn]", DaxLexemeKind.ColumnReference, "Col]umn")]
    [InlineData("\"a\"\"b\"", DaxLexemeKind.String, "a\"b")]
    [InlineData("[Broken", DaxLexemeKind.ColumnReference, "Broken")]
    [InlineData("'Open", DaxLexemeKind.QuotedTable, "Open")]
    [InlineData("dt\"2024-01-01\"", DaxLexemeKind.DateTime, "dt\"2024-01-01\"")]
    [InlineData("@Risk", DaxLexemeKind.QueryParameter, "@Risk")]
    [InlineData("1.5E+10", DaxLexemeKind.Number, "1.5E+10")]
    [InlineData("NORM.DIST", DaxLexemeKind.Identifier, "NORM.DIST")]
    public void Read_SingleToken_HasKindAndUnescapedName(string dax, DaxLexemeKind kind, string name)
    {
        var lexeme = Assert.Single(DaxLexicalTokens.Read(dax));

        Assert.Equal(kind, lexeme.Kind);
        Assert.Equal(name, lexeme.Name);
        Assert.Equal(dax, lexeme.Source);
        Assert.Equal(0, lexeme.Start);
        Assert.Equal(dax.Length - 1, lexeme.End);
    }

    [Fact]
    public void Read_KeepsSourceCasing_WhereTheEngineNormalizes()
    {
        var lexemes = DaxLexicalTokens.Read("sum(true)");

        Assert.Equal(["sum", "(", "true", ")"], lexemes.Select(l => l.Source));
        Assert.Equal("sum", lexemes[0].Name);
    }

    [Fact]
    public void Read_DropsCommentsAndEndOfFile_WithExactSpans()
    {
        const string dax = "// a [x]\n'T' . [C] /* 'U' */ -- [y]";
        var lexemes = DaxLexicalTokens.Read(dax);

        Assert.Equal(
            [DaxLexemeKind.QuotedTable, DaxLexemeKind.Dot, DaxLexemeKind.ColumnReference],
            lexemes.Select(l => l.Kind));
        Assert.All(lexemes, l => Assert.Equal(l.Source, dax[l.Start..(l.End + 1)]));
    }

    [Fact]
    public void Read_Empty_YieldsNothing()
        => Assert.Empty(DaxLexicalTokens.Read(""));
}
