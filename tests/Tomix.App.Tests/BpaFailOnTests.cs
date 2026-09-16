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
}
