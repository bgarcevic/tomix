using Tomix.App.Bpa;
using Tomix.Core.Bpa;

namespace Tomix.App.Tests;

/// <summary>
/// Shared fail-on parsing and threshold semantics for the BPA gates: <c>bpa run --fail-on</c>
/// and <c>deploy --bpa-fail-on</c> must accept the same values and block on the same severities.
/// </summary>
public sealed class BpaFailOnTests
{
    [Theory]
    [InlineData(null, BpaSeverity.Error)]
    [InlineData("", BpaSeverity.Error)]
    [InlineData("error", BpaSeverity.Error)]
    [InlineData("ERROR", BpaSeverity.Error)]
    [InlineData("warning", BpaSeverity.Warning)]
    [InlineData("Warning", BpaSeverity.Warning)]
    public void TryParse_AcceptsThresholdValues(string? value, BpaSeverity expected)
    {
        var parsed = BpaFailOn.TryParse(value, "--fail-on", out var severity, out var error);

        Assert.True(parsed);
        Assert.Equal(expected, severity);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("fatal")]
    [InlineData("errors")]
    public void TryParse_RejectsAnythingButErrorOrWarning(string value)
    {
        var parsed = BpaFailOn.TryParse(value, "--bpa-fail-on", out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"Invalid --bpa-fail-on value '{value}'. Expected: error or warning.", error);
    }

    [Fact]
    public void Blocking_ErrorThreshold_ErrorsOnly()
    {
        var blocking = BpaFailOn.Blocking(Violations(), BpaSeverity.Error);

        Assert.Equal([BpaSeverity.Error], blocking.Select(v => v.Severity).ToArray());
    }

    [Fact]
    public void Blocking_WarningThreshold_WarningsAndErrors()
    {
        var blocking = BpaFailOn.Blocking(Violations(), BpaSeverity.Warning);

        Assert.Equal([BpaSeverity.Warning, BpaSeverity.Error], blocking.Select(v => v.Severity).ToArray());
    }

    private static BpaViolation[] Violations()
    {
        var info = new BpaViolation("i", "i", "cat", BpaSeverity.Info, "Table", "i", "i");
        var warning = new BpaViolation("w", "w", "cat", BpaSeverity.Warning, "Table", "w", "w");
        var error = new BpaViolation("e", "e", "cat", BpaSeverity.Error, "Table", "e", "e");
        return [info, warning, error];
    }

    // ----- Issue #253: a rule that cannot be evaluated must not pass silently -----
    // The gates see BpaRunResult.Violations, so an unevaluable rule has to be projected into that
    // same visible stream as an error-severity finding to fail bpa run and deploy closed.

    [Fact]
    public void Violations_CompilationErrorSentinel_ProjectedAsErrorSeverityFinding()
    {
        var result = RunResult(BpaResult.Sentinel(
            BpaResultKind.CompilationError, Rule("BROKEN"), "Unexpected token '='", "Column"));

        var finding = Assert.Single(result.Violations);
        Assert.Equal("BROKEN", finding.RuleId);
        Assert.Equal(BpaSeverity.Error, finding.Severity);
        Assert.False(finding.CanFix);
        Assert.Contains("could not be evaluated", finding.Description);
        Assert.Contains("Unexpected token '='", finding.Description);
    }

    [Fact]
    public void Violations_RuleErrorAcrossScopes_CollapsesToOneFindingPerRule()
    {
        // The engine emits one sentinel per scope; a broken rule is one finding, not one per scope.
        var result = RunResult(
            BpaResult.Sentinel(BpaResultKind.CompilationError, Rule("BROKEN"), "Unexpected token '='", "Column"),
            BpaResult.Sentinel(BpaResultKind.EvaluationError, Rule("BROKEN"), "Sequence contains no elements", "Measure"));

        var finding = Assert.Single(result.Violations);
        Assert.Equal("BROKEN", finding.RuleId);
        Assert.Contains("Unexpected token '='", finding.Description);
        Assert.Contains("Sequence contains no elements", finding.Description);
    }

    [Fact]
    public void Violations_RuleErrorFinding_UsesErrorSeverityNotRuleSeverity()
    {
        // A warning-severity rule that cannot be evaluated is still an error-severity finding:
        // the failure of the gate machinery must not inherit the broken rule's own threshold.
        var warningRule = new BpaRule("BROKEN", "broken", "test", BpaSeverity.Warning, ["Column"]);
        var result = RunResult(BpaResult.Sentinel(BpaResultKind.CompilationError, warningRule, "boom", "Column"));

        Assert.All(result.Violations, v => Assert.Equal(BpaSeverity.Error, v.Severity));
    }

    [Fact]
    public void Blocking_RuleErrorOnly_BlocksAtBothThresholds()
    {
        var result = RunResult(BpaResult.Sentinel(
            BpaResultKind.CompilationError, Rule("BROKEN"), "boom", "Model"));

        Assert.Single(BpaFailOn.Blocking(result.Violations, BpaSeverity.Error));
        Assert.Single(BpaFailOn.Blocking(result.Violations, BpaSeverity.Warning));
    }

    [Fact]
    public void Blocking_PartialEvaluationError_RealViolationsPlusRuleError()
    {
        var result = RunResult(
            BpaResult.ForViolation(Rule("REAL"), new BpaViolation("REAL", "real", "cat", BpaSeverity.Warning, "Table", "t", "t")),
            BpaResult.Sentinel(BpaResultKind.EvaluationError, Rule("BROKEN"), "threw", "Column"));

        Assert.Equal(2, result.Violations.Count);
        // Default threshold: only the error-severity rule-error finding crosses it.
        var blocking = BpaFailOn.Blocking(result.Violations, BpaSeverity.Error);
        Assert.Equal(["BROKEN"], blocking.Select(v => v.RuleId).ToArray());
    }

    [Fact]
    public void Violations_DisabledAndCompatibilitySkips_StayNonBlocking()
    {
        // Disabled rules and compatibility-level skips are intentional, not failures —
        // they must keep riding the diagnostics footer without blocking a gate.
        var result = RunResult(
            BpaResult.Sentinel(BpaResultKind.DisabledRule, Rule("OFF")),
            BpaResult.Sentinel(BpaResultKind.InvalidCompatibilityLevel, Rule("OLD")));

        Assert.Empty(result.Violations);
        Assert.Empty(BpaFailOn.Blocking(result.Violations, BpaSeverity.Warning));
    }

    private static BpaRunResult RunResult(params BpaResult[] results)
        => new(results, "model", RulesEvaluated: 1);

    private static BpaRule Rule(string id)
        => new(id, id.ToLowerInvariant(), "test", BpaSeverity.Warning, ["Column"]);
}
