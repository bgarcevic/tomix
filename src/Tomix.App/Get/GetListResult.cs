using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.App.Get;

/// <summary>
/// The objects a list read selected (<c>get</c> with a wildcard, container or <c>--ls</c>, or the
/// <c>ls</c> shortcut), plus the model they came from.
/// </summary>
public sealed record GetListResult(
    string ModelName,
    int CompatibilityLevel,
    IReadOnlyList<GetListObject> Objects,
    [property: JsonIgnore] IReadOnlySet<string>? MeasureNames = null);

/// <summary>
/// A flat, render-ready projection of a matched <see cref="ModelObject"/>. The child tree itself is
/// dropped so the JSON contract stays a flat list; <see cref="ChildCounts"/> preserves a per-kind
/// tally (e.g. a table's column/measure/partition counts) for richer rendering.
/// <see cref="Projected"/> carries the catalog projection a single-object read also uses — JSON/CSV
/// output must read from it so a list row and an object's properties cannot drift.
/// </summary>
public sealed record GetListObject(
    string Path,
    string Name,
    ModelObjectKind Kind,
    string? Detail,
    string? Expression,
    string? Description,
    bool Hidden,
    string? SourceColumn,
    IReadOnlyDictionary<ModelObjectKind, int> ChildCounts,
    IReadOnlyDictionary<string, object?> Projected);
