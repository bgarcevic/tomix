namespace Tomix.Core.Bpa;

/// <summary>
/// A single raw analyzer result. For <see cref="BpaResultKind.Violation"/> the
/// <see cref="Violation"/> payload is populated and <see cref="IsIgnored"/> indicates whether the
/// violating object suppresses this rule via an object-level ignore annotation. For the sentinel
/// kinds (disabled / invalid-compatibility / compilation / evaluation) the payload is null and
/// <see cref="ErrorMessage"/> / <see cref="ErrorScope"/> carry the diagnostic detail.
/// </summary>
public sealed record BpaResult(
    BpaResultKind Kind,
    string RuleId,
    string RuleName,
    string Category,
    BpaSeverity Severity,
    BpaViolation? Violation = null,
    string? ErrorMessage = null,
    string? ErrorScope = null,
    bool IsIgnored = false)
{
    /// <summary>Creates a violation result from a fully-built <see cref="BpaViolation"/>.</summary>
    public static BpaResult ForViolation(BpaRule rule, BpaViolation violation, bool isIgnored = false)
        => new(BpaResultKind.Violation, rule.Id, rule.Name, rule.Category, rule.Severity,
            Violation: violation, IsIgnored: isIgnored);

    /// <summary>Creates a non-violation sentinel result that carries no model object.</summary>
    public static BpaResult Sentinel(BpaResultKind kind, BpaRule rule, string? errorMessage = null, string? errorScope = null)
        => new(kind, rule.Id, rule.Name, rule.Category, rule.Severity,
            ErrorMessage: errorMessage, ErrorScope: errorScope);
}
