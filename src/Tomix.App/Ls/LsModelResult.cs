using Tomix.Core.Models;

namespace Tomix.App.Ls;

/// <summary>The matched objects for an <c>ls</c> invocation, plus the model they came from.</summary>
public sealed record LsModelResult(
    string ModelName,
    int CompatibilityLevel,
    IReadOnlyList<LsObject> Objects);

/// <summary>
/// A flat, render-ready projection of a matched <see cref="ModelObject"/>. The child tree itself is
/// dropped so the JSON contract stays a flat list; <see cref="ChildCounts"/> preserves a per-kind
/// tally (e.g. a table's column/measure/partition counts) for richer rendering.
/// </summary>
public sealed record LsObject(
    string Path,
    string Name,
    ModelObjectKind Kind,
    string? Detail,
    string? Expression,
    string? Description,
    bool Hidden,
    string? SourceColumn,
    IReadOnlyDictionary<ModelObjectKind, int> ChildCounts,
    IReadOnlyDictionary<string, string>? Properties);
