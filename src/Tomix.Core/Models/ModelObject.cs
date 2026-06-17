namespace Tomix.Core.Models;

/// <summary>
/// A single navigable object in a model snapshot. Nodes form a tree rooted at tables and
/// model-level collections; an object path is resolved by walking this tree.
/// </summary>
/// <param name="Name">The object's own name (without any path prefix).</param>
/// <param name="Kind">The kind of object.</param>
/// <param name="Path">The fully qualified, slash-separated path to this object.</param>
/// <param name="Detail">A short single-line summary for display (data type, cardinality, permission, ...).</param>
/// <param name="Expression">A multi-line expression (e.g. measure DAX); <c>null</c> when the object has none.</param>
/// <param name="Description">The object's description in the model; <c>null</c> when none.</param>
/// <param name="Hidden">Whether the object is hidden in the model.</param>
/// <param name="Children">Child objects (e.g. a table's columns/measures, a role's members).</param>
public sealed record ModelObject(
    string Name,
    ModelObjectKind Kind,
    string Path,
    string? Detail,
    string? Expression,
    string? Description,
    bool Hidden,
    string? SourceColumn,
    IReadOnlyList<ModelObject> Children,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    public string? Property(string key)
        => Properties is not null && Properties.TryGetValue(key, out var value) ? value : null;
}
