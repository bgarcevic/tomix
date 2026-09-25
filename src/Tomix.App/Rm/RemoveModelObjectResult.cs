using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Rm;

/// <summary>
/// <paramref name="ObjectPath"/> is the object the command addressed. It serializes as <c>removed</c>
/// once the edit was saved or staged and as <c>wouldRemove</c> for a preview or dry run. When the
/// model is unchanged (<c>--if-exists</c> on a missing object) it serializes as <c>path</c>
/// alongside <c>reason</c>.
/// </summary>
public sealed record RemoveModelObjectResult(
    [property: JsonIgnore]
    string? ObjectPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CascadeRemoved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? RemainingPolicyPartitions = null) : MutationResult
{
    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Removed => IfApplied(ObjectPath);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WouldRemove => IfPreviewed(ObjectPath);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path => Status == MutationStatus.Unchanged ? ObjectPath : null;
}
