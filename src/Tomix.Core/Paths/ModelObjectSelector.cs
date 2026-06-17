using Tomix.Core.Models;

namespace Tomix.Core.Paths;

/// <summary>
/// Resolves an object-path filter against a <see cref="ModelSnapshot"/>. The algorithm walks the
/// snapshot tree segment by segment:
/// <list type="bullet">
///   <item>a keyword pivots to a collection (all objects of that kind at the root, or the matching
///   child kind further down);</item>
///   <item>a name filters the current collection, or — when it follows another name — descends one
///   level into children;</item>
///   <item>a path ending in an exact literal expands a matched container to its children
///   (so <c>Sales</c> lists Sales's contents while <c>Sa*</c> lists the tables themselves).</item>
/// </list>
/// An optional <paramref name="type"/> narrows the result to a single kind.
/// </summary>
public static class ModelObjectSelector
{
    public static IReadOnlyList<ModelObject> Select(
        ModelSnapshot snapshot,
        string? pathFilter,
        ModelObjectKind? type)
    {
        var segments = ObjectPath.Parse(pathFilter);

        IEnumerable<ModelObject> result = segments.Count == 0
            ? type is { } seed ? AllOfKind(snapshot, seed) : Tables(snapshot)
            : Resolve(snapshot, segments);

        if (type is { } filter)
            result = result.Where(o => o.Kind == filter);

        return result.ToList();
    }

    private static IReadOnlyList<ModelObject> Resolve(ModelSnapshot snapshot, IReadOnlyList<PathSegment> segments)
    {
        IEnumerable<ModelObject> current = Tables(snapshot); // implicit "Tables" collection
        var lastWasName = false;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            if (seg.TryGetKeyword(out var kind))
            {
                current = i == 0
                    ? AllOfKind(snapshot, kind)
                    : current.SelectMany(n => n.Children).Where(n => n.Kind == kind);
                lastWasName = false;
            }
            else
            {
                current = lastWasName
                    ? current.SelectMany(n => n.Children).Where(n => seg.NameMatch(n.Name))
                    : current.Where(n => seg.NameMatch(n.Name));
                lastWasName = true;
            }
        }

        // A terminal exact literal "enters" any matched container and lists its children;
        // leaves (and wildcard/keyword terminals) are returned as-is.
        if (segments[^1].IsExactLiteral)
            return current.SelectMany(n => n.Children.Count > 0 ? n.Children : [n]).ToList();

        return current.ToList();
    }

    private static IEnumerable<ModelObject> Tables(ModelSnapshot snapshot)
        => snapshot.Objects.Where(o => o.Kind == ModelObjectKind.Table);

    private static IReadOnlyList<ModelObject> AllOfKind(ModelSnapshot snapshot, ModelObjectKind kind)
    {
        var matches = new List<ModelObject>();

        void Walk(IReadOnlyList<ModelObject> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind == kind)
                    matches.Add(node);

                if (node.Children.Count > 0)
                    Walk(node.Children);
            }
        }

        Walk(snapshot.Objects);
        return matches;
    }
}
