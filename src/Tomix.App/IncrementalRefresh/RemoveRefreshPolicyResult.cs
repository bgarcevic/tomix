using System.Text.Json.Serialization;

namespace Tomix.App.IncrementalRefresh;

/// <param name="Removed">The table name when the policy was removed; false otherwise.</param>
/// <param name="RemainingPolicyPartitions">Policy-generated partitions the removal leaves
/// behind (they hold real data; removing the policy must not silently drop them).</param>
public sealed record RemoveRefreshPolicyResult(
    object Removed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Saved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? RemainingPolicyPartitions = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Reverted = false);
