using System.Text.Json.Serialization;

namespace Tomix.App.Set;

public sealed record SetModelPropertyResult(
    string Set,
    string Property,
    string Value,
    object Saved,
    int ValidationErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged = null);
