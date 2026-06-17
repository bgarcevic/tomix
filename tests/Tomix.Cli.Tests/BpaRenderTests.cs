using Tomix.Cli.Output;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Tests;

public class BpaRenderTests
{
    private static BpaViolation Violation(
        string ruleId,
        BpaSeverity severity,
        string objectName,
        string category = "Cat",
        string? ruleName = null,
        string? description = null)
        => new(
            ruleId,
            ruleName ?? ruleId,
            category,
            severity,
            ObjectType: "Column",
            ObjectName: objectName,
            ObjectPath: objectName,
            Description: description);

    [Fact]
    public void OrderRuleGroups_OrdersBySeverityThenCategoryThenName()
    {
        var violations = new[]
        {
            Violation("INFO_B", BpaSeverity.Info, "o1", category: "Maintenance", ruleName: "B rule"),
            Violation("ERR_A", BpaSeverity.Error, "o2", category: "DAX", ruleName: "A rule"),
            Violation("WARN_C", BpaSeverity.Warning, "o3", category: "Perf", ruleName: "C rule"),
            Violation("ERR_A", BpaSeverity.Error, "o4", category: "DAX", ruleName: "A rule"),
        };

        var groups = BpaRunView.OrderRuleGroups(violations);

        Assert.Equal(3, groups.Count);
        Assert.Equal("ERR_A", groups[0].RuleId);
        Assert.Equal("WARN_C", groups[1].RuleId);
        Assert.Equal("INFO_B", groups[2].RuleId);

        // Objects for the same rule are grouped together.
        Assert.Equal(new[] { "o2", "o4" }, groups[0].Objects);
    }

    [Fact]
    public void OrderRuleGroups_BreaksTiesByCategoryThenName()
    {
        var violations = new[]
        {
            Violation("R2", BpaSeverity.Warning, "o1", category: "Zeta", ruleName: "Alpha"),
            Violation("R1", BpaSeverity.Warning, "o2", category: "Alpha", ruleName: "Zeta"),
            Violation("R3", BpaSeverity.Warning, "o3", category: "Alpha", ruleName: "Beta"),
        };

        var groups = BpaRunView.OrderRuleGroups(violations);

        // Alpha category first; within it, "Beta" before "Zeta".
        Assert.Equal(new[] { "R3", "R1", "R2" }, groups.Select(g => g.RuleId).ToArray());
    }

    [Fact]
    public void FormatObjectList_UnderCap_ShowsAll()
    {
        var names = new[] { "a", "b", "c" };
        Assert.Equal("a · b · c", BpaRunView.FormatObjectList(names, full: false, cap: 10));
    }

    [Fact]
    public void FormatObjectList_OverCap_TruncatesWithCount()
    {
        var names = Enumerable.Range(1, 13).Select(i => $"o{i}").ToArray();

        var result = BpaRunView.FormatObjectList(names, full: false, cap: 10);

        Assert.StartsWith("o1 · o2 · ", result);
        Assert.EndsWith(" · … +3 more", result);
        Assert.DoesNotContain("o11", result);
    }

    [Fact]
    public void FormatObjectList_Full_ShowsAllEvenOverCap()
    {
        var names = Enumerable.Range(1, 13).Select(i => $"o{i}").ToArray();

        var result = BpaRunView.FormatObjectList(names, full: true, cap: 10);

        Assert.Contains("o13", result);
        Assert.DoesNotContain("more", result);
    }

    [Fact]
    public void FormatObjectList_Empty_ReturnsEmpty()
        => Assert.Equal("", BpaRunView.FormatObjectList(Array.Empty<string>(), full: false));

    [Theory]
    [InlineData("[Performance] Do not use X", "Performance", "Do not use X")]
    [InlineData("[performance] Do not use X", "Performance", "Do not use X")] // case-insensitive
    [InlineData("[Other] Do not use X", "Performance", "[Other] Do not use X")] // mismatch kept
    [InlineData("No prefix here", "Performance", "No prefix here")]
    public void StripCategoryPrefix_RemovesOnlyMatchingPrefix(string ruleName, string category, string expected)
        => Assert.Equal(expected, BpaRunView.StripCategoryPrefix(ruleName, category));

    [Fact]
    public void Guidance_StripsTrailingReference()
    {
        var result = BpaRunView.Guidance("Do the thing. Reference: https://example.com", collapse: false);
        Assert.Equal("Do the thing.", result);
    }

    [Fact]
    public void Guidance_Collapse_KeepsFirstLineOnly()
    {
        var result = BpaRunView.Guidance("First line.\nSecond line.", collapse: true);
        Assert.Equal("First line.", result);
    }

    [Fact]
    public void Guidance_NoCollapse_KeepsAllLines()
    {
        var result = BpaRunView.Guidance("First line.\r\nSecond line.", collapse: false);
        Assert.Equal("First line.\nSecond line.", result);
    }

    [Fact]
    public void WrapText_WrapsAtWidthWithoutSplittingWords()
    {
        var lines = BpaRunView.WrapText("the quick brown fox jumps", 10);

        Assert.All(lines, l => Assert.True(l.Length <= 10, $"line too long: '{l}'"));
        Assert.Equal("the quick brown fox jumps", string.Join(" ", lines));
    }

    [Fact]
    public void WrapText_ShortText_ReturnsSingleLine()
        => Assert.Equal(new[] { "short" }, BpaRunView.WrapText("short", 84).ToArray());

    [Fact]
    public void WrapText_OverlongWord_KeptOnOwnLine()
    {
        var lines = BpaRunView.WrapText("a supercalifragilistic b", 8);
        Assert.Contains("supercalifragilistic", lines);
    }

    [Fact]
    public void WrapText_PreservesExistingLineBreaks()
    {
        var lines = BpaRunView.WrapText("one\r\ntwo", 84);
        Assert.Equal(new[] { "one", "two" }, lines.ToArray());
    }

    [Fact]
    public void WrapText_Empty_ReturnsEmpty()
        => Assert.Empty(BpaRunView.WrapText("", 84));

    [Fact]
    public void MatchesFilter_NoFlags_ShowsEverything()
    {
        Assert.True(BpaRunView.MatchesFilter(BpaSeverity.Error, false, false, false));
        Assert.True(BpaRunView.MatchesFilter(BpaSeverity.Info, false, false, false));
    }

    [Fact]
    public void MatchesFilter_RespectsSelectedSeverities()
    {
        Assert.True(BpaRunView.MatchesFilter(BpaSeverity.Error, errors: true, warnings: false, info: false));
        Assert.False(BpaRunView.MatchesFilter(BpaSeverity.Warning, errors: true, warnings: false, info: false));
        Assert.True(BpaRunView.MatchesFilter(BpaSeverity.Warning, errors: true, warnings: true, info: false));
    }
}
