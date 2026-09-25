using System.Text.Json.Serialization;
using Tomix.App.Mutations;
using Tomix.Core.Models;

namespace Tomix.App.Replace;

public sealed record ReplaceModelTextResult(
    string Pattern,
    string Replacement,
    int ChangeCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ModelReplacePreview>? Previews) : MutationResult;
