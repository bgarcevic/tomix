using System.Text.Json.Serialization;

namespace Tomix.App.Set;

public sealed record SetModelPropertyResult(
    string Set,
    string Property,
    string Value,
    object Saved,
    int ValidationErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null);
