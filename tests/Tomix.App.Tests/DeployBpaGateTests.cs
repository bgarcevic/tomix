using Tomix.App.Deploy;
using Tomix.Core.Bpa;

namespace Tomix.App.Tests;

/// <summary>
/// Branch-complete coverage for <see cref="DeployModelHandler.EvaluateBpaGate"/>. This is the
/// pure decision logic extracted from the BPA deploy gate; it encodes the policy that
/// <c>--fix-bpa</c> blocks only on remaining error-severity violations, while the no-fix path
/// blocks on any violation. Previously the gate's fail condition was suppressed entirely under
/// <c>--fix-bpa</c>, allowing deploys with known violations.
/// </summary>
public sealed class DeployBpaGateTests
{
    private static readonly BpaViolation ErrorA = V("error-A", BpaSeverity.Error);
    private static readonly BpaViolation ErrorB = V("error-B", BpaSeverity.Error);
    private static readonly BpaViolation Warn = V("warn", BpaSeverity.Warning);
    private static readonly BpaViolation Info = V("info", BpaSeverity.Info);

    private static BpaViolation V(string id, BpaSeverity severity)
        => new(id, id, "cat", severity, "Table", id, id);

    [Fact]
    public void NoViolations_NoFix_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: false));
    }

    [Fact]
    public void NoViolations_FixBpa_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([], null, fixBpa: true));
    }

    [Fact]
    public void AnyViolation_NoFix_Fails()
    {
        var result = DeployModelHandler.EvaluateBpaGate([Warn], null, fixBpa: false);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void ErrorViolation_NoFix_Fails()
    {
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA], null, fixBpa: false);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Contains("1 violation(s)", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_AllErrorsRemediated_Proceeds()
    {
        // Pre-fix had two errors; post-fix re-evaluation found none.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [], fixBpa: true));
    }

    [Fact]
    public void FixBpa_OnlyWarningsRemain_Proceeds()
    {
        // Error was remediated; a warning survives but warnings do not block the fix path.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Warn], [Warn], fixBpa: true));
    }

    [Fact]
    public void FixBpa_OnlyInfoRemains_Proceeds()
    {
        Assert.Null(DeployModelHandler.EvaluateBpaGate([ErrorA, Info], [Info], fixBpa: true));
    }

    [Fact]
    public void FixBpa_ErrorRemains_Fails()
    {
        // One error fixed, one error could not be fixed -> must block the deploy.
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [ErrorB], fixBpa: true);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 error-severity violation(s) remaining", result.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_MultipleErrorsRemain_Fails()
    {
        var result = DeployModelHandler.EvaluateBpaGate([ErrorA, ErrorB], [ErrorA, ErrorB], fixBpa: true);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("2 error-severity violation(s) remaining", result!.Diagnostics[0].Message);
    }

    [Fact]
    public void FixBpa_NullPostFix_TreatedAsEmpty()
    {
        // Defensive: RunBpaGate always supplies post-fix violations when fixBpa is set, but the
        // helper treats a null post-fix set as empty (no remaining errors -> proceed). This pins
        // that contract so a future refactor cannot accidentally flip it to fail-open on errors.
        Assert.Null(DeployModelHandler.EvaluateBpaGate([Warn], null, fixBpa: true));
    }
}
