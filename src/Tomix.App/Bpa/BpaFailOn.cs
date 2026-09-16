using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

/// <summary>
/// Shared severity-threshold ("fail-on") semantics for the BPA gates: a gate blocks only on
/// violations at or above the threshold, where error (the default) blocks on error-severity
/// findings and warning also blocks on warnings. Parsing and filtering live here so
/// <c>bpa run --fail-on</c> and <c>deploy --bpa-fail-on</c> cannot drift apart.
/// </summary>
public static class BpaFailOn
{
    public static bool TryParse(string? value, string optionName, out BpaSeverity severity, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            severity = BpaSeverity.Error;
            error = null;
            return true;
        }

        if (value.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            severity = BpaSeverity.Warning;
            error = null;
            return true;
        }

        severity = BpaSeverity.Error;
        error = $"Invalid {optionName} value '{value}'. Expected: error or warning.";
        return false;
    }

    /// <summary>The violations a gate with this threshold must block on.</summary>
    public static IReadOnlyList<BpaViolation> Blocking(IReadOnlyList<BpaViolation> violations, BpaSeverity threshold)
        => threshold == BpaSeverity.Warning
            ? [.. violations.Where(v => v.Severity is BpaSeverity.Warning or BpaSeverity.Error)]
            : [.. violations.Where(v => v.Severity == BpaSeverity.Error)];
}
