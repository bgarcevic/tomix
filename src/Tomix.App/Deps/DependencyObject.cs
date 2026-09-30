namespace Tomix.App.Deps;

/// <summary>
/// A node in a dependency list/tree. <see cref="Children"/> is empty for single-level results and
/// for nodes that were already expanded elsewhere in the tree (cycle/diamond break).
/// </summary>
public sealed record DependencyObject(
    string Path,
    string Type,
    string Reference,
    IReadOnlyList<DependencyObject> Children);
