using Tomix.App.Bpa;
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
    public void ObjectLines_UnderCap_ShowsAllAndSkipsBlankNames()
        => Assert.Equal(["a", "b", "c"], BpaRunView.ObjectLines(["a", "", "b", "c"], full: false, cap: 10));

    [Fact]
    public void ObjectLines_OverCap_TruncatesWithCount()
    {
        var names = Enumerable.Range(1, 13).Select(i => $"o{i}").ToArray();

        var lines = BpaRunView.ObjectLines(names, full: false, cap: 10);

        Assert.Equal(11, lines.Count);
        Assert.Equal("o10", lines[9]);
        Assert.Equal("… +3 more", lines[10]);
    }

    [Fact]
    public void ObjectLines_Full_ShowsAllEvenOverCap()
    {
        var names = Enumerable.Range(1, 13).Select(i => $"o{i}").ToArray();

        Assert.Equal(names, BpaRunView.ObjectLines(names, full: true, cap: 10));
    }

    [Fact]
    public void OrderRuleGroups_CountsFixableObjects()
    {
        var violations = new[]
        {
            Violation("R1", BpaSeverity.Warning, "a") with { CanFix = true },
            Violation("R1", BpaSeverity.Warning, "b"),
            Violation("R2", BpaSeverity.Warning, "c") with { CanFix = true },
        };

        var groups = BpaRunView.OrderRuleGroups(violations);

        Assert.Equal("1 fixable", BpaRunView.FixableLabel(groups.Single(g => g.RuleId == "R1")));
        Assert.Equal("fixable", BpaRunView.FixableLabel(groups.Single(g => g.RuleId == "R2")));
    }

    [Fact]
    public void FixableLabel_NoneFixable_IsEmpty()
        => Assert.Equal("", BpaRunView.FixableLabel(BpaRunView.OrderRuleGroups([Violation("R1", BpaSeverity.Error, "a")])[0]));

    [Fact]
    public void SeveritySections_GroupsBySeverityWithObjectCounts()
    {
        var violations = new[]
        {
            Violation("I1", BpaSeverity.Info, "a"),
            Violation("E1", BpaSeverity.Error, "b"),
            Violation("E1", BpaSeverity.Error, "c"),
            Violation("E2", BpaSeverity.Error, "d"),
        };

        var sections = BpaRunView.SeveritySections(BpaRunView.OrderRuleGroups(violations));

        Assert.Equal([BpaSeverity.Error, BpaSeverity.Info], sections.Select(s => s.Severity));
        Assert.Equal(2, sections[0].Groups.Count);
        Assert.Equal(3, sections[0].ObjectCount);
        Assert.Equal(1, sections[1].ObjectCount);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 27, 326, "All 27 rules passed · 326ms")]
    [InlineData(0, 0, 0, 0, 1, 0, "All 1 rule passed")]
    [InlineData(3, 32, 0, 5, 27, 326, "3 errors · 32 warnings in 5 of 27 rules · 22 passed · 326ms")]
    [InlineData(0, 1, 2, 2, 10, 0, "1 warning · 2 info in 2 of 10 rules · 8 passed")]
    // A rule-error finding can come from a rule outside the evaluated count.
    [InlineData(1, 0, 0, 1, 0, 0, "1 error in 1 of 1 rule · 0 passed")]
    public void SummaryLine_ReportsCountsAndPassedRules(
        int errors, int warnings, int info, int failed, int evaluated, long durationMs, string expected)
        => Assert.Equal(expected, BpaRunView.SummaryLine(errors, warnings, info, failed, evaluated, durationMs));

    [Theory]
    // #266: rules skipped for missing VertiPaq statistics are never counted as passed.
    [InlineData(0, 27, 3, "24 of 27 rules passed · 3 not checked · 326ms")]
    [InlineData(5, 27, 3, "3 errors · 32 warnings in 5 of 27 rules · 19 passed · 3 not checked · 326ms")]
    public void SummaryLine_NotCheckedRules_AreNotCountedAsPassed(int failed, int evaluated, int notChecked, string expected)
    {
        var (errors, warnings) = failed == 0 ? (0, 0) : (3, 32);
        Assert.Equal(expected, BpaRunView.SummaryLine(errors, warnings, 0, failed, evaluated, 326, notChecked));
    }

    [Fact]
    public void PackSegments_NeverSplitsASegment()
    {
        var lines = BpaRunView.PackSegments(["A_VERY_LONG_RULE_IDENTIFIER", "Category", "", "fixable"], width: 30);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["A_VERY_LONG_RULE_IDENTIFIER"], lines[0]);
        Assert.Equal(["Category", "fixable"], lines[1]);
    }

    [Theory]
    [InlineData(null, "tx bpa run --details")]
    [InlineData(new[] { "bpa", "run", "samples/My Model.pbip", "--ruleset", "full" },
        "tx bpa run 'samples/My Model.pbip' --ruleset full --details")]
    // Display, fix, and persistence flags (with their values) never leak into a hint.
    [InlineData(new[] { "bpa", "run", "m.bim", "--full", "--fix", "--save-to", "out", "-y", "--errors" },
        "tx bpa run m.bim --details")]
    [InlineData(new[] { "bpa", "run", "m.bim", "--save-to=old.bim", "--trx=old.trx", "--fix=true" },
        "tx bpa run m.bim --details")]
    [InlineData(new[] { "bpa", "run", "m.bim", "--fix", "--dry-run", "--allow-delete" },
        "tx bpa run m.bim --details")]
    [InlineData(new[] { "bpa", "run", "Sales$(whoami).bim" },
        "tx bpa run 'Sales$(whoami).bim' --details")]
    public void HintCommand_EchoesTheCommandLine(string[]? tokens, string expected)
        => Assert.Equal(expected, BpaRunView.HintCommand(tokens, "--details"));

    [Fact]
    public void HintCommand_QuotesRuleIdAddedByRenderer()
        => Assert.Equal("tx bpa run --rule 'RULE_$X'",
            BpaRunView.HintCommand(null, "--rule", "RULE_$X"));

    [Fact]
    public void HintCommand_EscapesApostropheForHostShell()
    {
        var expected = OperatingSystem.IsWindows()
            ? "tx bpa run 'O''Brien.bim' --details"
            : "tx bpa run 'O'\\''Brien.bim' --details";

        Assert.Equal(expected, BpaRunView.HintCommand(["bpa", "run", "O'Brien.bim"], "--details"));
    }

    [Fact]
    public void PendingFix_PropertySet_ShowsHeadlineAndBeforeAfter()
    {
        var (headline, change) = BpaRunView.PendingFix(new BpaFixChange(
            "FORMAT_RULE", "Measure", "Sales/Total", BpaFixAction.Set, "FormatString", Before: "", After: "#,##0"));

        Assert.Equal("Would fix: Measure 'Sales/Total' — FORMAT_RULE", headline);
        Assert.Equal("FormatString: \"\" → \"#,##0\"", change);
    }

    [Fact]
    public void PendingFix_Delete_HasNoChangeLine()
    {
        var (headline, change) = BpaRunView.PendingFix(new BpaFixChange(
            "UNUSED", "Column", "Sales/Key", BpaFixAction.Delete));

        Assert.Equal("Would delete: Column 'Sales/Key' — UNUSED", headline);
        Assert.Null(change);
    }

    [Fact]
    public void PendingFix_UnsetAndMultilineLongValues_StayOnOneBoundedLine()
    {
        var expression = "VAR x =\r\n    1\r\nRETURN\n" + new string('x', 100);

        var (_, change) = BpaRunView.PendingFix(new BpaFixChange(
            "R", "Measure", "M", BpaFixAction.Set, "Expression", Before: null, After: expression));

        Assert.StartsWith("Expression: (unset) → \"VAR x = 1 RETURN xxx", change);
        Assert.EndsWith("…\"", change);
        Assert.DoesNotContain('\n', change!);
    }

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
