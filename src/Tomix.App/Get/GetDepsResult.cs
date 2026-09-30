using Tomix.App.Deps;

namespace Tomix.App.Get;

/// <summary>
/// Dependency analysis for a single object. <see cref="Upstream"/>/<see cref="Downstream"/> are
/// single-level by default; with deep analysis each <see cref="DependencyObject"/> carries its own
/// <see cref="DependencyObject.Children"/> to form a recursive tree. <see cref="Unused"/> is
/// populated instead when running in <c>get --unused</c> mode (no target object).
/// </summary>
public sealed record GetDepsResult(
    string Path,
    string Type,
    IReadOnlyList<DependencyObject> Upstream,
    IReadOnlyList<DependencyObject> Downstream,
    IReadOnlyList<DependencyObject>? Unused = null);
