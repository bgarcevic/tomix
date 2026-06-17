namespace Tomix.App.Deps;

/// <summary>
/// Dependency analysis for a single object. <see cref="Upstream"/>/<see cref="Downstream"/> are
/// single-level by default; with deep analysis each <see cref="DependencyObject"/> carries its own
/// <see cref="DependencyObject.Children"/> to form a recursive tree. <see cref="Unused"/> is
/// populated instead when running in <c>--unused</c> mode (no target object).
/// </summary>
public sealed record DepsModelResult(
    string Path,
    string Type,
    IReadOnlyList<DependencyObject> Upstream,
    IReadOnlyList<DependencyObject> Downstream,
    IReadOnlyList<DependencyObject>? Unused = null);

/// <summary>
/// A node in a dependency list/tree. <see cref="Children"/> is empty for single-level results and
/// for nodes that were already expanded elsewhere in the tree (cycle/diamond break).
/// </summary>
public sealed record DependencyObject(
    string Path,
    string Type,
    string Reference,
    IReadOnlyList<DependencyObject> Children);
