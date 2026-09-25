using System.Text.Json.Serialization;

namespace Tomix.App.Set;

public sealed record SetModelPropertyResult(
    string Set,
    string Property,
    string Value,
    object Saved,
    // Post-mutation error count from the shared offline analyzer (the measurement the save
    // gate reuses for delta semantics). Null when never measured: --revert discards the
    // staged copy without opening the model.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ValidationErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? FixedReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? DryRun = null,
    // Render-only: the current value of a DAX property before the edit, for the text preview.
    [property: JsonIgnore]
    string? OldValue = null,
    // Render-only: whether the edited property carries DAX (see SetModelPropertyHandler).
    [property: JsonIgnore]
    bool IsDaxProperty = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Tomix.Core.Models.RefreshPolicyInfo? Policy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CreatedExpressions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? NewValidationErrors = null);
