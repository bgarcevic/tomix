using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.Core.Models;

namespace Tomix.App.Deps;

/// <summary>
/// A precomputed dependency graph over a flattened model snapshot. Forward edges record "depends
/// on" (an object's DAX references plus relationship participation); reverse edges record
/// "referenced by". This is the engine behind <c>tx deps</c> and is the analogue of Tabular
/// Editor's <c>DependsOnList</c>/<c>ReferencedByList</c>, including a cycle-safe recursive
/// <see cref="Deep"/> traversal (its <c>Deep()</c>) and an <see cref="Unused"/> query.
/// </summary>
internal sealed class DependencyGraph
{
    private readonly IReadOnlyList<ModelObject> _objects;
    private readonly Dictionary<string, ModelObject> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelObject> _measureByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelObject> _tableByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelObject>> _columnsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelObject>> _forward = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelObject>> _reverse = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sortByTargets = new(StringComparer.OrdinalIgnoreCase);

    public DependencyGraph(IReadOnlyList<ModelObject> objects)
    {
        _objects = objects;
        IndexObjects();
        BuildEdges();
    }

    public static DependencyGraph FromSnapshot(ModelSnapshot snapshot)
        => new(ModelObjectProjection.Flatten(snapshot));

    /// <summary>Single-level objects that <paramref name="target"/> depends on.</summary>
    public IReadOnlyList<DependencyObject> DirectUpstream(ModelObject target)
        => Neighbors(_forward, target).Select(o => ToDependency(o, [])).ToList();

    /// <summary>Single-level objects that reference <paramref name="target"/>.</summary>
    public IReadOnlyList<DependencyObject> DirectDownstream(ModelObject target)
        => Neighbors(_reverse, target).Select(o => ToDependency(o, [])).ToList();

    /// <summary>
    /// Every DAX property text that references <paramref name="target"/>, with the exact
    /// reference spans — the input to a rename rewrite. Unlike <see cref="DirectDownstream"/>,
    /// relationship participation is excluded: relationships bind to objects, not names, so a
    /// rename cannot break them.
    /// </summary>
    public IReadOnlyList<ReferenceSite> SitesReferencing(ModelObject target)
        => SitesReferencingAny([target]);

    /// <summary>
    /// Sites referencing any of <paramref name="targets"/> in one pass — a removal impact check
    /// covers a table plus its measures and columns. Targets themselves are never sources.
    /// </summary>
    public IReadOnlyList<ReferenceSite> SitesReferencingAny(IReadOnlyList<ModelObject> targets)
    {
        var keyed = targets.Select(t => (Target: t, Key: Key(t))).ToList();
        var sites = new List<ReferenceSite>();

        foreach (var obj in _objects)
        {
            var objKey = Key(obj);
            if (keyed.Any(t => Eq(objKey, t.Key)))
                continue;

            foreach (var site in DaxExpressions.Sites(obj))
            {
                var references = DaxReferenceExtractor.Extract(site.Expression)
                    .Where(r => keyed.Any(t => ReferencesTarget(r, t.Target, t.Key)))
                    .ToList();

                if (references.Count > 0)
                    sites.Add(new ReferenceSite(obj, site.Property, site.Expression, references));
            }
        }

        return sites;
    }

    private bool ReferencesTarget(
        DaxReferenceExtractor.DaxReference reference, ModelObject target, string targetKey)
    {
        // A qualified reference ('Region'[Amount]) resolves to the child object, but its table
        // part references the table by name too — a table rename must rewrite it even when the
        // child itself doesn't resolve.
        if (target.Kind == ModelObjectKind.Table && reference.Table is { } table
            && string.Equals(table, target.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        return Resolve(reference) is { } resolved && Eq(Key(resolved), targetKey);
    }

    /// <summary>
    /// Recursive dependency tree. <paramref name="upstream"/> follows "depends on"; otherwise
    /// "referenced by". Each object is expanded once: a node already seen on this traversal (a
    /// cycle, or a shared diamond branch) appears as a childless leaf, mirroring the set-based
    /// recursion in Tabular Editor's <c>Deep()</c>.
    /// </summary>
    public IReadOnlyList<DependencyObject> Deep(ModelObject target, bool upstream, int maxDepth)
    {
        var adjacency = upstream ? _forward : _reverse;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(target) };
        return BuildLevel(Key(target), adjacency, expanded, depth: 1, maxDepth);
    }

    /// <summary>
    /// Measures and columns that nothing references. A column is excluded when it participates in a
    /// relationship, a hierarchy, or another column's sort-by, so structural usage is not reported
    /// as unused.
    /// </summary>
    public IReadOnlyList<DependencyObject> Unused(bool hiddenOnly)
    {
        var result = new List<DependencyObject>();
        foreach (var obj in _objects)
        {
            if (obj.Kind is not (ModelObjectKind.Measure or ModelObjectKind.Column))
                continue;
            if (hiddenOnly && !obj.Hidden)
                continue;
            if (_reverse.ContainsKey(Key(obj)))
                continue;
            if (obj.Kind == ModelObjectKind.Column && IsColumnStructurallyUsed(obj))
                continue;

            result.Add(ToDependency(obj, []));
        }

        return result
            .OrderBy(d => d.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<DependencyObject> BuildLevel(
        string sourceKey,
        Dictionary<string, List<ModelObject>> adjacency,
        HashSet<string> expanded,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth || !adjacency.TryGetValue(sourceKey, out var neighbors))
            return [];

        var nodes = new List<DependencyObject>(neighbors.Count);
        foreach (var neighbor in neighbors)
        {
            var neighborKey = Key(neighbor);
            if (!expanded.Add(neighborKey))
            {
                nodes.Add(ToDependency(neighbor, []));
                continue;
            }

            var children = BuildLevel(neighborKey, adjacency, expanded, depth + 1, maxDepth);
            nodes.Add(ToDependency(neighbor, children));
        }

        return nodes;
    }

    private void IndexObjects()
    {
        foreach (var obj in _objects)
        {
            _byPath[Key(obj)] = obj;

            if (obj.Kind == ModelObjectKind.Table)
                _tableByName.TryAdd(obj.Name, obj);
            else if (obj.Kind == ModelObjectKind.Measure)
                _measureByName.TryAdd(obj.Name, obj);
            else if (obj.Kind == ModelObjectKind.Column)
            {
                if (!_columnsByName.TryGetValue(obj.Name, out var list))
                    _columnsByName[obj.Name] = list = [];
                list.Add(obj);

                var sortBy = obj.Property("SortByColumn");
                if (!string.IsNullOrWhiteSpace(sortBy))
                    _sortByTargets.Add(Key(SiblingPath(obj.Path, sortBy!)));
            }
        }
    }

    private void BuildEdges()
    {
        foreach (var obj in _objects)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var expression in DaxExpressions.ForObject(obj))
            {
                foreach (var reference in DaxReferenceExtractor.Extract(expression))
                {
                    var resolved = Resolve(reference);
                    if (resolved is not null && !Eq(Key(resolved), Key(obj)))
                        targets.Add(Key(resolved));
                }
            }

            if (obj.Kind == ModelObjectKind.Relationship)
            {
                AddColumnTarget(obj.Property("FromColumn"), targets);
                AddColumnTarget(obj.Property("ToColumn"), targets);
            }

            if (targets.Count == 0)
                continue;

            var resolvedTargets = targets
                .Select(p => _byPath[p])
                .OrderBy(o => o.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _forward[Key(obj)] = resolvedTargets;
            foreach (var target in resolvedTargets)
            {
                if (!_reverse.TryGetValue(Key(target), out var dependents))
                    _reverse[Key(target)] = dependents = [];
                dependents.Add(obj);
            }
        }

        foreach (var dependents in _reverse.Values)
            dependents.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
    }

    private ModelObject? Resolve(DaxReferenceExtractor.DaxReference reference)
    {
        switch (reference.Shape)
        {
            case DaxReferenceShape.Qualified:
                var path = ModelObjectLookup.NormalizePath($"{reference.Table}/{reference.Object}");
                return _byPath.GetValueOrDefault(path);

            // 'Table' is always a table; a bare word only counts when a table by that name
            // exists (the extractor already dropped VAR names, keywords, and function calls).
            case DaxReferenceShape.Table:
            case DaxReferenceShape.TableCandidate:
                return _tableByName.GetValueOrDefault(reference.Table!);

            default:
                // A lone [X] is a measure first (measures are referenced unqualified), else a column.
                if (_measureByName.TryGetValue(reference.Object!, out var measure))
                    return measure;
                if (_columnsByName.TryGetValue(reference.Object!, out var columns) && columns.Count > 0)
                    return columns[0];

                return null;
        }
    }

    private void AddColumnTarget(string? columnReference, HashSet<string> targets)
    {
        if (string.IsNullOrWhiteSpace(columnReference))
            return;

        var path = ModelObjectLookup.NormalizePath(columnReference);
        if (_byPath.ContainsKey(path))
            targets.Add(path);
    }

    private bool IsColumnStructurallyUsed(ModelObject column)
        => !string.IsNullOrEmpty(column.Property("UsedInHierarchies"))
           || string.Equals(column.Property("UsedInRelationships"), "true", StringComparison.OrdinalIgnoreCase)
           || _sortByTargets.Contains(Key(column));

    private static IReadOnlyList<ModelObject> Neighbors(
        Dictionary<string, List<ModelObject>> adjacency,
        ModelObject obj)
        => adjacency.TryGetValue(Key(obj), out var list) ? list : [];

    private static DependencyObject ToDependency(ModelObject obj, IReadOnlyList<DependencyObject> children)
        => new(obj.Path, ModelObjectProjection.KindLabel(obj.Kind), ToDaxReference(obj), children);

    private static string ToDaxReference(ModelObject obj)
    {
        var parts = obj.Path.Split('/', 2);
        if (parts.Length == 2 && obj.Kind is ModelObjectKind.Column or ModelObjectKind.Measure)
            return $"'{parts[0]}'[{parts[1]}]";

        return obj.Name;
    }

    // The sibling of "Table/Col" named <name> is "Table/<name>".
    private static string SiblingPath(string path, string name)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? name : $"{path[..slash]}/{name}";
    }

    private static string Key(ModelObject obj) => Key(obj.Path);

    private static string Key(string path) => ModelObjectLookup.NormalizePath(path);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One DAX property whose text references a target object, with the exact spans.</summary>
internal sealed record ReferenceSite(
    ModelObject Source,
    string Property,
    string Expression,
    IReadOnlyList<DaxReferenceExtractor.DaxReference> References);
