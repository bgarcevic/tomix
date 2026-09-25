using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Add;

/// <summary>
/// <paramref name="Path"/> is the object the command addressed. It serializes as <c>added</c>
/// once the edit was saved or staged and as <c>wouldAdd</c> for a preview or dry run;
/// <paramref name="ExistingPath"/> is set when <c>--if-not-exists</c> left the model unchanged.
/// </summary>
public sealed record AddModelObjectResult(
    [property: JsonIgnore]
    string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExistingPath = null) : MutationResult
{
    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Added => IfApplied(Path);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WouldAdd => IfPreviewed(Path);
}
