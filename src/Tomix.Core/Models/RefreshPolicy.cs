namespace Tomix.Core.Models;

/// <summary>
/// Provider-agnostic view of a table's incremental refresh policy. <see cref="Issues"/> carries
/// the provider's validation findings so read (show) and write (set) paths report the same facts.
/// Absent expressions are <c>""</c>, never null, matching the property-bag convention.
/// </summary>
public sealed record RefreshPolicyInfo(
    string Table,
    string Mode,
    string RollingWindowGranularity,
    int RollingWindowPeriods,
    string IncrementalGranularity,
    int IncrementalPeriods,
    int IncrementalOffset,
    string PollingExpression,
    string SourceExpression,
    IReadOnlyList<string> PolicyPartitions,
    IReadOnlyList<RefreshPolicyIssue> Issues);

/// <summary>
/// One validation finding. <paramref name="Code"/> is a lowercase snake token
/// (e.g. <c>range_parameter_missing</c>); <paramref name="Severity"/> is "error" or "warning".
/// These are result payload, not TOMIX_ diagnostic codes.
/// </summary>
public sealed record RefreshPolicyIssue(
    string Code,
    string Severity,
    string Message)
{
    public const string SeverityError = "error";
    public const string SeverityWarning = "warning";

    public bool IsError => Severity == SeverityError;
}

/// <summary>
/// Create-or-edit request. Null fields are left untouched on edit; on create the periods,
/// granularities, and source expression are required (missing ones become blocking issues).
/// <paramref name="Force"/> saves despite validation errors.
/// </summary>
public sealed record RefreshPolicySetRequest(
    string Table,
    string? Mode,
    string? RollingWindowGranularity,
    int? RollingWindowPeriods,
    string? IncrementalGranularity,
    int? IncrementalPeriods,
    int? IncrementalOffset,
    string? PollingExpression,
    string? SourceExpression,
    bool Force);

/// <param name="CreatedExpressions">Names of shared expressions the provider scaffolded
/// (RangeStart/RangeEnd) because the model was missing them.</param>
public sealed record RefreshPolicySetResult(
    RefreshPolicyInfo Policy,
    bool Created,
    IReadOnlyList<string> CreatedExpressions);

/// <summary>
/// Thrown by providers when a policy write has validation errors and the request did not
/// pass Force. The mutation runner maps it to TOMIX_REFRESH_POLICY_INVALID.
/// </summary>
public sealed class RefreshPolicyValidationException : Exception
{
    public IReadOnlyList<RefreshPolicyIssue> Issues { get; }

    public RefreshPolicyValidationException(string message, IReadOnlyList<RefreshPolicyIssue> issues)
        : base(message)
    {
        Issues = issues;
    }
}

/// <summary>
/// Thrown when an operation targets a table that has no incremental refresh policy. Callers
/// (the mutation runner for <c>rm</c>, the apply handler) map it to TOMIX_REFRESH_POLICY_NOT_FOUND,
/// keeping it distinct from generic mutation/apply failures so the documented code is emitted.
/// </summary>
public sealed class RefreshPolicyNotFoundException : Exception
{
    public RefreshPolicyNotFoundException(string message)
        : base(message)
    {
    }
}

/// <param name="EffectiveDate">Date the policy is evaluated against; null = today.</param>
/// <param name="Refresh">False = bootstrap: create/merge partition definitions without loading data.</param>
public sealed record RefreshPolicyApplyRequest(
    string Table,
    DateOnly? EffectiveDate,
    bool Refresh,
    int? MaxParallelism = null);

public sealed record RefreshPolicyApplyResult(
    string Server,
    string Database,
    string Table,
    DateOnly EffectiveDate,
    bool Refreshed,
    IReadOnlyList<string> Operations,
    long DurationMs);
