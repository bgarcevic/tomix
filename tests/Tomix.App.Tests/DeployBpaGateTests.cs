using Tomix.App.Deploy;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;

namespace Tomix.App.Tests;

/// <summary>
/// Branch-complete coverage for <see cref="DeployModelHandler.EvaluateBpaGate"/>. This is the
/// pure decision logic extracted from the BPA deploy gate; it encodes the policy that both
/// gate phases — the pre-deploy check and the re-check after <c>--fix-bpa</c> fixes — block
/// only on violations at or above the <c>--bpa-fail-on</c> threshold (error by default).
/// Previously the no-fix path blocked on any violation while the fix path blocked only on
/// error-severity violations, so the same finding could pass one phase and fail the other.
/// </summary>
public sealed class DeployBpaGateTests
{
    private static readonly BpaViolation ErrorA = V("error-A", RuleSeverity.Error);
    private static readonly BpaViolation ErrorB = V("error-B", RuleSeverity.Error);
    private static readonly BpaViolation Warn = V("warn", RuleSeverity.Warning);
    private static readonly BpaViolation Info = V("info", RuleSeverity.Info);

    private static BpaViolation V(string id, RuleSeverity severity)
        => new(id, id, "cat", severity, "Table", id, id);

    /// <summary>The single rule the threshold matrix turns on: the gate blocks at or above the threshold, never below it.</summary>
    [Theory]
    [InlineData(RuleSeverity.Error, RuleSeverity.Info, false)]
    [InlineData(RuleSeverity.Error, RuleSeverity.Warning, false)]
    [InlineData(RuleSeverity.Error, RuleSeverity.Error, true)]
    [InlineData(RuleSeverity.Warning, RuleSeverity.Info, false)]
    [InlineData(RuleSeverity.Warning, RuleSeverity.Warning, true)]
    [InlineData(RuleSeverity.Warning, RuleSeverity.Error, true)]
    public void PreDeployGate_BlocksExactlyAtOrAboveThreshold(
        RuleSeverity failOn, RuleSeverity severity, bool expectBlocked)
    {
        var result = DeployModelHandler.EvaluateBpaGate([V("v", severity)], null, fixBpa: false, failOn);

        Assert.Equal(expectBlocked, result is not null);
    }

    [Theory]
    [InlineData(RuleSeverity.Error)]
    [InlineData(RuleSeverity.Warning)]
    public void NoViolations_Proceeds_RegardlessOfThreshold(RuleSeverity failOn)
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: false, failOn));
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: true, failOn));
    }

    [Fact]
    public void DefaultThreshold_MixedFindings_FailsCountingOnlyErrors()
    {
        // Warnings and info ride along but only the error crosses the default threshold.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn, Info], null, fixBpa: false, RuleSeverity.Error);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 error-severity violation(s)", result.Diagnostics[0].Message);
        Assert.Contains("Use --fix-bpa to auto-fix or --skip-bpa to bypass.", result.Diagnostics[0].Message);
    }

    [Fact]
    public void WarningThreshold_MixedFindings_FailsCountingWarningsAndErrors()
    {
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn, Info], null, fixBpa: false, RuleSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("2 warning-severity or higher violation(s)", result.Diagnostics[0].Message);
        Assert.DoesNotContain("remaining after auto-fix", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_DefaultThreshold_AllErrorsRemediated_Proceeds()
    {
        // Pre-fix had two errors; post-fix re-evaluation found none.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [], fixBpa: true, RuleSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_OnlyWarningsRemain_Proceeds()
    {
        // Error was remediated; a warning survives but sits below the default threshold.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Warn], [Warn], fixBpa: true, RuleSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_OnlyInfoRemains_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Info], [Info], fixBpa: true, RuleSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_ErrorRemains_Fails()
    {
        // One error fixed, one error could not be fixed -> must block the deploy.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [ErrorB], fixBpa: true, RuleSeverity.Error);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 error-severity violation(s) remaining after auto-fix", result.Diagnostics[0].Message);
        Assert.Contains("Use --skip-bpa to bypass.", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_WarningThreshold_WarningRemains_Fails()
    {
        // The warning survived the fixes and the user raised the post-fix threshold to warning.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn], [Warn], fixBpa: true, RuleSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("1 warning-severity or higher violation(s) remaining after auto-fix", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_WarningThreshold_InfoRemains_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Info], [Info], fixBpa: true, RuleSeverity.Warning));
    }

    [Fact]
    public void FixBpa_WarningThreshold_WarningAndErrorRemain_FailsCountingBoth()
    {
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB, Warn], [ErrorA, Warn], fixBpa: true, RuleSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("2 warning-severity or higher violation(s) remaining after auto-fix", result!.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_NullPostFix_TreatedAsEmpty()
    {
        // Defensive: RunBpaGate always supplies post-fix violations when fixBpa is set, but the
        // helper treats a null post-fix set as empty (nothing remains -> proceed). This pins
        // that contract so a future refactor cannot accidentally flip it to fail-open on errors.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([Warn], null, fixBpa: true, RuleSeverity.Error));
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA], null, fixBpa: true, RuleSeverity.Error));
    }

    // ----- Issue #253: a rule that cannot be evaluated blocks the deploy and names itself -----
    // Rule-error findings arrive as error-severity violations (the BpaRunResult projection), so
    // the gate counts them like any other finding; the ruleErrors argument exists so the block
    // message can name the broken rule and its reason instead of an anonymous count.

    private static readonly BpaViolation BrokenRule = new(
        "BROKEN_RULE", "broken rule", "test", RuleSeverity.Error, "Column", "", "",
        Description: "Rule could not be evaluated: Column: Unexpected token '='");

    [Fact]
    public void RuleErrorOnly_BlockedByDefault_NamesRuleAndReason()
    {
        var result = DeployModelHandler.EvaluateBpaGate(
            [BrokenRule], null, fixBpa: false, RuleSeverity.Error, ruleErrors: [BrokenRule]);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 error-severity violation(s)", result.Diagnostics[0].Message);
        Assert.Contains("could not be evaluated", result.Diagnostics[0].Message);
        Assert.Contains("broken rule", result.Diagnostics[0].Message);
        Assert.Contains("BROKEN_RULE", result.Diagnostics[0].Message);
        Assert.Contains("Unexpected token '='", result.Diagnostics[0].Message);
    }

    [Fact]
    public void RuleError_BlocksAtWarningThresholdToo()
    {
        var result = DeployModelHandler.EvaluateBpaGate(
            [BrokenRule], null, fixBpa: false, RuleSeverity.Warning, ruleErrors: [BrokenRule]);

        Assert.NotNull(result);
        Assert.Equal(1, result!.ExitCode);
    }

    [Fact]
    public void RuleError_SurvivingFixBpa_BlocksRemainingAfterAutoFix()
    {
        // --fix-bpa cannot repair a rule that cannot be evaluated: the post-fix re-evaluation
        // still reports it, and the gate must block naming it.
        var result = DeployModelHandler.EvaluateBpaGate(
            [BrokenRule], [BrokenRule], fixBpa: true, RuleSeverity.Error, ruleErrors: [BrokenRule]);

        Assert.NotNull(result);
        Assert.Contains("remaining after auto-fix", result!.Diagnostics[0].Message);
        Assert.Contains("BROKEN_RULE", result.Diagnostics[0].Message);
    }

    [Fact]
    public void RuleErrors_Absent_MessageStaysAnonymous()
    {
        // Real violations (no rule errors) keep the existing count-only message.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA], null, fixBpa: false, RuleSeverity.Error, ruleErrors: []);

        Assert.NotNull(result);
        Assert.DoesNotContain("could not be evaluated", result!.Diagnostics[0].Message);
    }

    [Fact]
    public void Block_NamesRulesNotCheckedForMissingVertipaqStats()
    {
        // #266: a block must not imply the statistics rules passed; it names them with the fix.
        var notChecked = new[] { StatsSentinel("LARGE_TABLES_SHOULD_BE_PARTITIONED") };

        var result = DeployModelHandler.EvaluateBpaGate([ErrorA], null, fixBpa: false, RuleSeverity.Error,
            notChecked: notChecked);

        Assert.NotNull(result);
        Assert.Contains("Not checked (no VertiPaq statistics): LARGE_TABLES_SHOULD_BE_PARTITIONED.",
            result!.Diagnostics[0].Message);
    }

    [Fact]
    public void MissingVertipaqStatsWarning_NamesRulesWithHint_AndIsEmptyWhenAllChecked()
    {
        Assert.Empty(DeployModelHandler.MissingVertipaqStatsWarning([]));

        var warning = Assert.Single(DeployModelHandler.MissingVertipaqStatsWarning(
            [StatsSentinel("RULE_A"), StatsSentinel("RULE_B")]));

        Assert.Equal("TOMIX_BPA_VERTIPAQ_STATS_MISSING", warning.Code);
        Assert.Equal(Tomix.Core.Diagnostics.DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("2 rule(s)", warning.Message);
        Assert.Contains("RULE_A, RULE_B", warning.Message);
        Assert.Contains("tx vertipaq --annotate --save", warning.Hint);
        // A plain model file cannot run `tx vertipaq`; the hint must say how it gets statistics.
        Assert.Contains("workspace mode", warning.Hint);
    }

    [Theory]
    [InlineData("Sales.Prod", true)]
    [InlineData("ssas01.contoso.com", false)]
    [InlineData("MyWorkspace", false)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales.Prod", false)]
    public void AmbiguousServerWarning_NamesTheServerAndTheWorkspaceAlternative(string server, bool warns)
    {
        var warnings = DeployModelHandler.AmbiguousServerWarning(server);

        if (!warns)
        {
            Assert.Empty(warnings);
            return;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal("TOMIX_DEPLOY_AMBIGUOUS_SERVER", warning.Code);
        Assert.Equal(Tomix.Core.Diagnostics.DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains($"'{server}'", warning.Message);
        Assert.Contains($"powerbi://api.powerbi.com/v1.0/myorg/{server}", warning.Hint);
    }

    private static BpaResult StatsSentinel(string id)
        => BpaResult.Sentinel(BpaResultKind.MissingVertipaqStats,
            new BpaRule(id, id, "Performance", RuleSeverity.Warning, ["Table"]));
}
