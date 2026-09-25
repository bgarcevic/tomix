using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Mv;

/// <summary>
/// <paramref name="Source"/> serializes as <c>moved</c> once the edit was saved or staged and as
/// <c>wouldMove</c> for a preview or dry run.
/// </summary>
public sealed record MoveModelObjectResult(
    [property: JsonIgnore]
    string Source,
    string To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? FixedReferences = null) : MutationResult
{
    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moved => IfApplied(Source);

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WouldMove => IfPreviewed(Source);
}
