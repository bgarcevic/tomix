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
}
