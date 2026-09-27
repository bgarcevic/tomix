using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxSyntaxCheckTests
{
    [Theory]
    [InlineData("SUM(Sales[Amount]) + [Total]")]
    [InlineData("COUNTROWS('Order Lines')")]
    [InlineData("[Weird ]] Name] & \"a\"\"b\"")]
    [InlineData("VAR x = {1, 2} RETURN COUNTROWS(x)")]
    [InlineData("TOPN(1, Sales, Sales[Amount], , [Price])")]
    [InlineData("IF(x > 1, {1, 2}, {3})")]
    [InlineData("dt\"2024-01-01\"")]
    [InlineData("'It''s'[A] // apostrophes and brackets in names are fine")]
    [InlineData("Sales[E] + 1e5 - -1")]
    public void Analyze_ValidDax_HasNoIssues(string dax)
        => Assert.Empty(DaxSyntaxCheck.Analyze(dax));

    [Fact]
    public void Analyze_MultilineCommentAndLineComments_AreValid()
    {
        const string dax = "/* multi\nline */ SUM(Sales[Amount]) -- trailing";

        Assert.Empty(DaxSyntaxCheck.Analyze(dax));
    }

    [Theory]
    [InlineData("SUM(Sales[Amount]")]
    [InlineData("( 1 + ( 2 * 3 )")]
    public void Analyze_UnclosedGroup_IsReportedAtTheOpeningSymbol(string dax)
    {
        var issues = DaxSyntaxCheck.Analyze(dax);

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnbalancedGroup, issue.Kind);
        Assert.Contains("'(' has no matching", issue.Message);
        Assert.Equal(dax.IndexOf('('), issue.Start);
    }

    [Fact]
    public void Analyze_UnclosedBrace_IsReportedAtTheOpeningBrace()
    {
        var issues = DaxSyntaxCheck.Analyze("{1, 2");

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnbalancedGroup, issue.Kind);
        Assert.Contains("'{' has no matching", issue.Message);
        Assert.Equal(0, issue.Start);
    }

    [Fact]
    public void Analyze_StrayCloser_HasNoMatchingOpener()
    {
        var issues = DaxSyntaxCheck.Analyze(") + 1");

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnbalancedGroup, issue.Kind);
        Assert.Contains("Closing ')'", issue.Message);
        Assert.Equal(0, issue.Start);
    }

    [Fact]
    public void Analyze_MismatchedNesting_ReportsBothProblems()
    {
        var issues = DaxSyntaxCheck.Analyze("( 1 }");

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, issue => issue.Message.Contains("Closing '}'"));
        Assert.Contains(issues, issue => issue.Message.Contains("'(' has no matching"));
    }

    [Theory]
    [InlineData("\"abc")]
    [InlineData("\"abc\"\"")]
    [InlineData("'Sales")]
    [InlineData("[Amount")]
    public void Analyze_UnterminatedLiteral_IsReported(string dax)
    {
        var issues = DaxSyntaxCheck.Analyze(dax);

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnterminatedLiteral, issue.Kind);
        Assert.Equal(0, issue.Start);
    }

    [Fact]
    public void Analyze_UnterminatedBlockComment_IsReported()
    {
        var issues = DaxSyntaxCheck.Analyze("1 + 1 /* never closed");

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnterminatedComment, issue.Kind);
    }

    [Fact]
    public void Analyze_IllegalCharacter_IsReportedWithItsOffset()
    {
        var issues = DaxSyntaxCheck.Analyze("1 # 2");

        var issue = Assert.Single(issues);
        Assert.Equal(DaxSyntaxErrorKind.UnexpectedCharacter, issue.Kind);
        Assert.Contains("#", issue.Message);
        Assert.Equal(2, issue.Start);
    }

    [Fact]
    public void Analyze_StringContent_IsNeverScannedForBrackets()
    {
        var issues = DaxSyntaxCheck.Analyze("\" unclosed ( bracket { inside \" & SUM(Sales[Amount])");

        Assert.Empty(issues);
    }

    // ── grammar errors from the parser (#203) ───────────────────────────────

    /// <summary>
    /// DAX the parser must accept: the quirks a grammar check is most likely to trip over. The
    /// sample-model corpus (OfflineDaxFormatterCorpusTests) covers real-world expressions.
    /// </summary>
    [Theory]
    [InlineData("VAR a = 1 VAR b = a + 1 RETURN b")]
    [InlineData("CALCULATE([Sales], Sales[Color] = \"Red\", ALL(Sales))")]
    [InlineData("[Sales] (Sales[Color] = \"Red\")")]
    [InlineData("'Date'[Year] IN {2023, 2024}")]
    [InlineData("NOT ISBLANK([X]) && -[Y] ^ 2 > 1 || !TRUE()")]
    [InlineData("IF(x, , 1)")]
    [InlineData("TOPN(1, Sales, Sales[Amount], , )")]
    [InlineData("SELECTEDMEASURE() * 1.1")]
    [InlineData("@Threshold + 1")]
    [InlineData("SUM(Sales[Amount]);")]
    [InlineData("INFO.TABLES()")]
    [InlineData("WINDOW(1, ABS, -1, REL, ALLSELECTED('Date'), ORDERBY('Date'[Date], ASC))")]
    [InlineData("Product[Category].[Subcategory]")]
    [InlineData("EVALUATE ROW(\"a\", 1) ORDER BY [a] DESC")]
    [InlineData("DEFINE MEASURE Sales[M] = 1 VAR v = 2 EVALUATE {[M] + v}")]
    [InlineData("DEFINE FUNCTION F = (x : SCALAR NUMERIC VAL) => x + 1 EVALUATE {F(1)}")]
    [InlineData("EVALUATE {1} EVALUATE {2}")]
    [InlineData("Total Sales := SUM(Sales[Amount])")]
    public void Analyze_ValidGrammar_HasNoIssues(string dax)
        => Assert.Empty(DaxSyntaxCheck.Analyze(dax));

    [Theory]
    [InlineData("1 +", DaxSyntaxErrorKind.MissingOperand, 3, "Expected an expression, but the expression ended.")]
    [InlineData("SUM(Sales[Amount] + )", DaxSyntaxErrorKind.MissingOperand, 20, "Expected an expression, but found ')'.")]
    [InlineData("1 * * 2", DaxSyntaxErrorKind.MissingOperand, 4, "Expected an expression, but found '*'.")]
    [InlineData("VAR x = RETURN x", DaxSyntaxErrorKind.MissingOperand, 8, "Expected an expression, but found 'RETURN'.")]
    [InlineData("SUM(Sales[Amount] Sales[Cost])", DaxSyntaxErrorKind.UnexpectedToken, 18, "Expected ',' or ')', but found 'Sales'.")]
    [InlineData("{1 2}", DaxSyntaxErrorKind.UnexpectedToken, 3, "Expected ',' or '}', but found '2'.")]
    [InlineData("SUM(Sales[Amount]) SUM(Sales[Cost])", DaxSyntaxErrorKind.UnexpectedToken, 19, "Unexpected 'SUM' after the end of the expression.")]
    [InlineData("VAR x = 1", DaxSyntaxErrorKind.IncompleteVarBlock, 9, "VAR block has no RETURN.")]
    [InlineData("VAR x 1 RETURN x", DaxSyntaxErrorKind.IncompleteVarBlock, 6, "Expected '=' after VAR x, but found '1'.")]
    public void Analyze_GrammarError_IsReportedAtTheOffendingToken(
        string dax, DaxSyntaxErrorKind kind, int start, string message)
    {
        var issue = Assert.Single(DaxSyntaxCheck.Analyze(dax));

        Assert.Equal(kind, issue.Kind);
        Assert.Equal(start, issue.Start);
        Assert.Equal(message, issue.Message);
    }

    [Fact]
    public void Analyze_GrammarErrors_ReportOnlyTheFirst()
    {
        // Recovery after a grammar error is guesswork, so later errors would be noise.
        var issue = Assert.Single(DaxSyntaxCheck.Analyze("SUM(a b c) 1 +"));

        Assert.Equal(6, issue.Start);
    }

    [Fact]
    public void Analyze_TokenIssues_SuppressGrammarErrors()
    {
        // An unclosed bracket already explains everything after it.
        var issue = Assert.Single(DaxSyntaxCheck.Analyze("SUM(1 +"));

        Assert.Equal(DaxSyntaxErrorKind.UnbalancedGroup, issue.Kind);
    }

    [Theory]
    [InlineData(DaxSyntaxErrorKind.UnexpectedCharacter, "lex", "unexpectedCharacter")]
    [InlineData(DaxSyntaxErrorKind.UnterminatedComment, "lex", "unterminatedComment")]
    [InlineData(DaxSyntaxErrorKind.UnbalancedGroup, "parse", "unbalancedGroup")]
    [InlineData(DaxSyntaxErrorKind.MissingOperand, "parse", "missingOperand")]
    public void Issue_ExposesStageAndCode(DaxSyntaxErrorKind kind, string stage, string code)
    {
        var issue = new DaxSyntaxIssue(0, 1, kind, "x");

        Assert.Equal(stage, issue.Stage);
        Assert.Equal(code, issue.Code);
    }
}
