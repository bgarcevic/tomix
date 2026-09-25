using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Set;

/// <summary>
/// <paramref name="ObjectPath"/> serializes as <c>set</c> once the edit was saved or staged and as
/// <c>wouldSet</c> for a preview or dry run; an unchanged or reverted result reports it as <c>path</c>.
/// </summary>
public sealed record SetModelPropertyResult(
    [property: JsonIgnore]
    string ObjectPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Property,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Value,
    // Post-mutation error count from the shared offline analyzer (the measurement the save
    // gate reuses for delta semantics). Null when never measured: --revert discards the
    // staged copy without opening the model.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ValidationErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? FixedReferences = null,
    // Render-only: the current value of a DAX property before the edit, for the text preview.
    [property: JsonIgnore]
    string? OldValue = null,
    // Render-only: whether the edited property carries DAX (see SetModelPropertyHandler).
    [property: JsonIgnore]
    bool IsDaxProperty = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Tomix.Core.Models.RefreshPolicyInfo? Policy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CreatedExpressions = null) : MutationResult
{
    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Set => IfApplied(ObjectPath);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WouldSet => IfPreviewed(ObjectPath);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path => Status is MutationStatus.Unchanged or MutationStatus.Reverted ? ObjectPath : null;
}
