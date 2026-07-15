using Tomix.Core.Models;

namespace Tomix.App.IncrementalRefresh;

/// <summary>
/// Null policy fields are left untouched when editing an existing policy; creating a new policy
/// requires periods, granularities, and a source expression. <paramref name="Force"/> saves
/// despite validation errors.
/// </summary>
public sealed record SetRefreshPolicyRequest(
    ModelReference Model,
    string Table,
    string? Mode,
    string? RollingWindowGranularity,
    int? RollingWindowPeriods,
    string? IncrementalGranularity,
    int? IncrementalPeriods,
    int? IncrementalOffset,
    string? PollingExpression,
    string? SourceExpression,
    bool Force,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false)
{
    public bool HasPolicyOptions =>
        Mode is not null
        || RollingWindowGranularity is not null
        || RollingWindowPeriods is not null
        || IncrementalGranularity is not null
        || IncrementalPeriods is not null
        || IncrementalOffset is not null
        || PollingExpression is not null
        || SourceExpression is not null;
}
