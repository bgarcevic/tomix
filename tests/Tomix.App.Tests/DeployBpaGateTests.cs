using Tomix.App.Deploy;
using Tomix.Core.Bpa;

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
    private static readonly BpaViolation ErrorA = V("error-A", BpaSeverity.Error);
    private static readonly BpaViolation ErrorB = V("error-B", BpaSeverity.Error);
    private static readonly BpaViolation Warn = V("warn", BpaSeverity.Warning);
    private static readonly BpaViolation Info = V("info", BpaSeverity.Info);

    private static BpaViolation V(string id, BpaSeverity severity)
        => new(id, id, "cat", severity, "Table", id, id);

    /// <summary>The single rule the threshold matrix turns on: the gate blocks at or above the threshold, never below it.</summary>
    [Theory]
    [InlineData(BpaSeverity.Error, BpaSeverity.Info, false)]
    [InlineData(BpaSeverity.Error, BpaSeverity.Warning, false)]
    [InlineData(BpaSeverity.Error, BpaSeverity.Error, true)]
    [InlineData(BpaSeverity.Warning, BpaSeverity.Info, false)]
    [InlineData(BpaSeverity.Warning, BpaSeverity.Warning, true)]
    [InlineData(BpaSeverity.Warning, BpaSeverity.Error, true)]
    public void PreDeployGate_BlocksExactlyAtOrAboveThreshold(
        BpaSeverity failOn, BpaSeverity severity, bool expectBlocked)
    {
        var result = DeployModelHandler.EvaluateBpaGate([V("v", severity)], null, fixBpa: false, failOn);

        Assert.Equal(expectBlocked, result is not null);
    }

    [Theory]
    [InlineData(BpaSeverity.Error)]
    [InlineData(BpaSeverity.Warning)]
    public void NoViolations_Proceeds_RegardlessOfThreshold(BpaSeverity failOn)
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: false, failOn));
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: true, failOn));
    }

    [Fact]
    public void DefaultThreshold_MixedFindings_FailsCountingOnlyErrors()
    {
        // Warnings and info ride along but only the error crosses the default threshold.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn, Info], null, fixBpa: false, BpaSeverity.Error);

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
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn, Info], null, fixBpa: false, BpaSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("2 warning-severity or higher violation(s)", result.Diagnostics[0].Message);
        Assert.DoesNotContain("remaining after auto-fix", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_DefaultThreshold_AllErrorsRemediated_Proceeds()
    {
        // Pre-fix had two errors; post-fix re-evaluation found none.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [], fixBpa: true, BpaSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_OnlyWarningsRemain_Proceeds()
    {
        // Error was remediated; a warning survives but sits below the default threshold.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Warn], [Warn], fixBpa: true, BpaSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_OnlyInfoRemains_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Info], [Info], fixBpa: true, BpaSeverity.Error));
    }

    [Fact]
    public void FixBpa_DefaultThreshold_ErrorRemains_Fails()
    {
        // One error fixed, one error could not be fixed -> must block the deploy.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [ErrorB], fixBpa: true, BpaSeverity.Error);

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
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, Warn], [Warn], fixBpa: true, BpaSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("1 warning-severity or higher violation(s) remaining after auto-fix", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_WarningThreshold_InfoRemains_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Info], [Info], fixBpa: true, BpaSeverity.Warning));
    }

    [Fact]
    public void FixBpa_WarningThreshold_WarningAndErrorRemain_FailsCountingBoth()
    {
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB, Warn], [ErrorA, Warn], fixBpa: true, BpaSeverity.Warning);

        Assert.NotNull(result);
        Assert.Contains("2 warning-severity or higher violation(s) remaining after auto-fix", result!.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_NullPostFix_TreatedAsEmpty()
    {
        // Defensive: RunBpaGate always supplies post-fix violations when fixBpa is set, but the
        // helper treats a null post-fix set as empty (nothing remains -> proceed). This pins
        // that contract so a future refactor cannot accidentally flip it to fail-open on errors.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([Warn], null, fixBpa: true, BpaSeverity.Error));
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA], null, fixBpa: true, BpaSeverity.Error));
    }
}
