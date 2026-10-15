using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Rm;

/// <summary>A batch target resolved against the snapshot. <paramref name="Object"/> is null when
/// the target was not found (<c>--if-exists</c>); <paramref name="SubsumedBy"/> names the target
/// whose removal takes this one with it (a column whose table is also removed).</summary>
internal sealed record PlannedRemoval(RemoveTarget Target, ModelObject? Object, string? SubsumedBy = null)
{
    public bool Removes => Object is not null && SubsumedBy is null;
}

/// <summary>
/// The whole deletion set of a batch <c>rm</c>, resolved and checked before anything is mutated:
/// every target found exactly once, duplicates and targets inside other targets folded away, and
/// the references from surviving objects that the removal would break. References between targets
/// are allowed — a table and its columns, a measure and the measures it calls, a data source and
/// the partitions bound to it — whatever order the targets were listed in.
/// </summary>
internal sealed class RemoveBatchPlan
{
    private readonly Dictionary<ModelObject, ModelObject?> _parents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ModelObject> _removed = new(ReferenceEqualityComparer.Instance);

    private RemoveBatchPlan(ModelSnapshot snapshot)
    {
        void Walk(IEnumerable<ModelObject> nodes, ModelObject? parent)
        {
            foreach (var node in nodes)
            {
                _parents[node] = parent;
                Walk(node.Children, node);
            }
        }

        Walk(snapshot.Objects, null);
        Flattened = ModelObjectProjection.Flatten(snapshot);
    }

    public IReadOnlyList<ModelObject> Flattened { get; }

    /// <summary>Every target in input order, duplicates dropped.</summary>
    public IReadOnlyList<PlannedRemoval> Targets { get; private set; } = [];

    /// <summary>The targets to remove, dependents first: relationships, levels, and partitions go
    /// before the columns, hierarchies, and data sources they hang off, so a target is rarely
    /// cascaded away by another target first.</summary>
    public IReadOnlyList<PlannedRemoval> ApplyOrder
        => Targets.Where(t => t.Removes).OrderBy(t => Rank(t.Object!.Kind)).ToList();

    /// <summary>
    /// Resolves every target. Missing targets fail the batch unless <paramref name="ifExists"/>;
    /// ambiguous ones always do. All failures are reported together.
    /// </summary>
    public static RemoveBatchPlan Resolve(ModelSnapshot snapshot, IReadOnlyList<RemoveTarget> targets, bool ifExists)
    {
        var plan = new RemoveBatchPlan(snapshot);
        var missing = new List<string>();
        var ambiguous = new List<string>();
        var resolved = new List<PlannedRemoval>();
        var seen = new HashSet<ModelObject>(ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            var matches = plan.Find(snapshot, target);
            if (matches.Count == 0)
            {
                if (ifExists)
                    resolved.Add(new PlannedRemoval(target, null));
                else
                    missing.Add(target.Label);
                continue;
            }

            if (matches.Count > 1)
            {
                ambiguous.Add($"{target.Label} ({string.Join(", ", matches.Select(m => KindName(m.Kind)).Distinct())})");
                continue;
            }

            if (matches[0].Kind == ModelObjectKind.Model)
                throw new ArgumentException("The model root cannot be removed.");
            if (seen.Add(matches[0]))
                resolved.Add(new PlannedRemoval(target, matches[0]));
        }

        if (ambiguous.Count > 0)
            throw new AmbiguousObjectException(
                $"{ambiguous.Count} target(s) match more than one object: {string.Join("; ", ambiguous)}. "
                + "Give a type ({\"type\": ...} in --from, or --type) or a container keyword (e.g. 'tables/T/partitions/P').");
        if (missing.Count > 0)
            throw new ObjectNotFoundException(
                $"{missing.Count} target(s) not found: {string.Join(", ", missing)}. Nothing was removed.",
                hint: "Run 'tx ls' to list objects, or pass --if-exists to skip missing targets.");

        // A target inside another target goes with it: drop it from the apply list but keep it in
        // the result, so every input line is accounted for.
        foreach (var removal in resolved.Where(r => r.Object is not null))
            plan._removed.Add(removal.Object!);
        plan.Targets = resolved
            .Select(r => r.Object is not null && plan.OutermostRemovedAncestor(r.Object) is { } parent
                ? r with { SubsumedBy = parent.Path }
                : r)
            .ToList();
        return plan;
    }

    /// <summary>Whether <paramref name="obj"/> goes with the batch: it is a target or inside one.</summary>
    public bool IsRemoved(ModelObject obj)
    {
        for (ModelObject? current = obj; current is not null; current = Parent(current))
        {
            if (_removed.Contains(current))
                return true;
        }

        return false;
    }

    /// <summary>Surviving objects whose DAX references a target.</summary>
    public IReadOnlyList<string> BrokenReferences()
        => RemoveGuard.ReferencingPaths(Flattened, Targets.Where(t => t.Removes).Select(t => t.Object!), IsRemoved);

    /// <summary>Surviving objects whose Power Query (M) refers to a shared-expression target.</summary>
    public Task<IReadOnlyList<string>> BrokenMReferencesAsync(CancellationToken cancellationToken)
        => MReferenceGuard.ReferencingPathsAsync(
            Flattened, Targets.Where(t => t.Removes).Select(t => t.Object!), IsRemoved, cancellationToken);

    /// <summary>Surviving partitions bound to a data-source target. Unlike DAX, a binding cannot
    /// dangle: the data source is not removable while one survives, <c>--force</c> or not.</summary>
    public IReadOnlyDictionary<ModelObject, IReadOnlyList<string>> BoundPartitions()
    {
        var bound = new Dictionary<ModelObject, IReadOnlyList<string>>(ReferenceEqualityComparer.Instance);
        foreach (var source in Targets.Where(t => t.Removes && t.Object!.Kind == ModelObjectKind.DataSource))
        {
            var partitions = Flattened
                .Where(o => o.Kind == ModelObjectKind.Partition
                    && string.Equals(o.Property("DataSourceName"), source.Object!.Name, StringComparison.OrdinalIgnoreCase)
                    && !IsRemoved(o))
                .Select(o => o.Path)
                .ToList();
            if (partitions.Count > 0)
                bound[source.Object!] = partitions;
        }

        return bound;
    }

    /// <summary>Whether a cascade entry changed an object the batch removes anyway (another
    /// target or something inside one), so it says nothing about what survives.</summary>
    public bool IsInternal(ModelRemoveCascade cascade)
    {
        var key = ModelObjectLookup.NormalizePath(cascade.Path);
        var matches = Flattened
            .Where(o => o.Kind.Matches(cascade.Kind)
                && string.Equals(ModelObjectLookup.NormalizePath(o.Path), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count > 0 && matches.All(IsRemoved);
    }

    public static string KindName(ModelObjectKind kind)
        => ModelObjectTypeCatalog.Discovery.FirstOrDefault(e => e.Kind == kind)?.Token ?? kind.ToString().ToLowerInvariant();

    private IReadOnlyList<ModelObject> Find(ModelSnapshot snapshot, RemoveTarget target)
    {
        var path = target.Path.Trim();
        if (path.Contains("->", StringComparison.Ordinal))
            return target.Type is null or ModelObjectKind.Relationship ? FindRelationship(path) : [];

        // A KPI shares its measure's path; '-t kpi' on the measure path means its KPI child.
        if (target.Type == ModelObjectKind.Kpi && !path.EndsWith("/KPI", StringComparison.OrdinalIgnoreCase))
            path += "/KPI";

        return ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), target.Type);
    }

    /// <summary>A relationship by its endpoints: <c>Sales[Key] -&gt; 'Product'[Key]</c>.</summary>
    private IReadOnlyList<ModelObject> FindRelationship(string path)
    {
        var sides = path.Split("->", 2);
        if (!DaxObjectForm.TryParse(sides[0], out var fromTable, out var fromColumn)
            || !DaxObjectForm.TryParse(sides[1], out var toTable, out var toColumn))
            return [];

        return Flattened
            .Where(o => o.Kind == ModelObjectKind.Relationship
                && SameColumn(o.Property(PropertyBagKeys.FromColumn), fromTable, fromColumn)
                && SameColumn(o.Property(PropertyBagKeys.ToColumn), toTable, toColumn))
            .ToList();
    }

    private static bool SameColumn(string? endpoint, string table, string column)
        => endpoint is not null
            && DaxObjectForm.TryParse(endpoint, out var t, out var c)
            && string.Equals(t, table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c, column, StringComparison.OrdinalIgnoreCase);

    private ModelObject? Parent(ModelObject obj) => _parents.GetValueOrDefault(obj);

    private ModelObject? OutermostRemovedAncestor(ModelObject obj)
    {
        ModelObject? outermost = null;
        for (var current = Parent(obj); current is not null; current = Parent(current))
        {
            if (_removed.Contains(current))
                outermost = current;
        }

        return outermost;
    }

    private static int Rank(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Relationship or ModelObjectKind.RoleMember or ModelObjectKind.TablePermission
            or ModelObjectKind.Kpi or ModelObjectKind.RefreshPolicy => 0,
        ModelObjectKind.Level or ModelObjectKind.Partition or ModelObjectKind.CalculationItem
            or ModelObjectKind.Calendar or ModelObjectKind.Perspective or ModelObjectKind.Culture => 1,
        ModelObjectKind.Measure or ModelObjectKind.Hierarchy => 2,
        ModelObjectKind.Column or ModelObjectKind.CalculatedColumn => 3,
        ModelObjectKind.Table => 5,
        ModelObjectKind.DataSource => 6,
        _ => 4
    };
}
