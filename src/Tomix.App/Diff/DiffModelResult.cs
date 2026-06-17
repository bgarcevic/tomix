using System.Text.Json.Serialization;

namespace Tomix.App.Diff;

public sealed record DiffModelResult(
    bool HasChanges,
    DiffSummary Summary,
    IReadOnlyList<DiffChange> Changes);

public sealed record DiffSummary(
    int Added,
    int Removed,
    int Modified);

public sealed record DiffChange(
    string Action,
    string ObjectType,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? OldValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? NewValue = null);
