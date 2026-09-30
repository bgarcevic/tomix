using Tomix.Core.Models;

namespace Tomix.App.Get;

/// <summary>
/// A read against one model. <paramref name="Path"/> selects the objects; <paramref name="Mode"/>
/// says what to do with them. <see cref="GetMode.Auto"/> lets the path decide: a path naming one
/// object reads it, a wildcard or container path (or no path) lists every match.
/// </summary>
/// <param name="Query">Project a single property; needs a single object.</param>
/// <param name="Type">Picks one object when a path matches several, or filters a list.</param>
/// <param name="Where">Property filters, ANDed; any filter makes the read a list.</param>
/// <param name="Direction">With <see cref="GetMode.Deps"/>: which side of the graph to trace.</param>
/// <param name="Deep">With <see cref="GetMode.Deps"/>: walk the chain recursively.</param>
/// <param name="MaxDepth">With <paramref name="Deep"/>: how far to walk.</param>
/// <param name="HiddenOnly">With <see cref="GetMode.Unused"/>: only hidden objects.</param>
public sealed record GetModelRequest(
    ModelReference Model,
    string? Path,
    string? Query,
    ModelObjectKind? Type,
    GetMode Mode = GetMode.Auto,
    IReadOnlyList<PropertyFilter>? Where = null,
    DepsDirection Direction = DepsDirection.Both,
    bool Deep = false,
    int MaxDepth = 10,
    bool HiddenOnly = false);

public enum GetMode
{
    /// <summary>The path decides between <see cref="Object"/> and <see cref="List"/>.</summary>
    Auto,

    /// <summary>Read one object's properties.</summary>
    Object,

    /// <summary>List every object the path selects.</summary>
    List,

    /// <summary>Trace what one object uses and what uses it.</summary>
    Deps,

    /// <summary>Find measures and columns nothing depends on (whole model, no path).</summary>
    Unused
}

public enum DepsDirection
{
    Both,
    Upstream,
    Downstream
}
