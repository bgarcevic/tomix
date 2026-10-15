using Tomix.App.Deps;
using Tomix.App.ModelObjects;
using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>
/// Thrown when a removal would leave DAX references pointing at a deleted object and
/// <c>--force</c> was not given. Mapped to <c>TOMIX_RM_BREAKS_REFS</c> by
/// <see cref="MutationRunner"/>.
/// </summary>
public sealed class RemoveBrokenReferencesException : Exception
{
    public RemoveBrokenReferencesException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a batch removal would cascade onto objects that survive it (relationships, sort-by,
/// hierarchy levels, perspectives, role permissions) and <c>--cascade</c> was not given. Mapped to
/// <c>TOMIX_RM_NEEDS_CASCADE</c> by <see cref="MutationRunner"/>.
/// </summary>
public sealed class RemoveCascadeRequiredException : Exception
{
    public RemoveCascadeRequiredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The DAX impact of removing an object. Unlike a rename there is nothing to rewrite — a
/// reference to a deleted object cannot be fixed — so downstream references block the removal
/// unless <c>--force</c>. Structural references (relationships, sort-by, hierarchy levels,
/// perspective and translation entries, role permissions) are not reported here: the provider
/// cascade-removes them so the model stays valid.
/// </summary>
internal static class RemoveGuard
{
    public static async Task<IReadOnlyList<string>> ReferencingPathsAsync(
        IModelSession session,
        string path,
        ModelObjectKind? type,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // The mutator accepts DAX bracket paths; the snapshot lookup does not — convert first.
        // Only DAX-named kinds leave text references behind, so filter to them BEFORE the
        // uniqueness test (same partition-shares-path guard as the rename fixup).
        var matches = ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), type)
            .Where(o => IsDaxNamed(o.Kind))
            .ToList();
        if (matches.Count != 1)
            return []; // not-found/ambiguous is the mutator's error to raise

        var target = matches[0];
        var flattened = ModelObjectProjection.Flatten(snapshot);

        // Deleting a table deletes its measures, columns, and calendars with it: references to
        // any of them break, while references from inside the table vanish along with it.
        // A calendar sharing a table's name blocks on the table's uses too — 'Fiscal' could mean
        // either, so the guard errs toward asking for --force.
        var targets = new List<ModelObject> { target };
        if (target.Kind == ModelObjectKind.Table)
            targets.AddRange(flattened.Where(o =>
                o.Kind is ModelObjectKind.Measure or ModelObjectKind.Column or ModelObjectKind.CalculatedColumn
                    or ModelObjectKind.Calendar
                && IsWithin(o.Path, target.Path)));

        var paths = new List<string>();
        foreach (var site in new DependencyGraph(flattened).SitesReferencingAny(targets))
        {
            if (IsWithin(site.Source.Path, target.Path))
                continue;
            if (!paths.Contains(site.Source.Path, StringComparer.OrdinalIgnoreCase))
                paths.Add(site.Source.Path);
        }

        return paths;
    }

    /// <summary>
    /// The DAX objects outside a removal set that reference anything in it — the batch form of
    /// <see cref="ReferencingPathsAsync"/>. <paramref name="roots"/> are the removed objects
    /// (a table stands for its measures, columns, and calendars too); <paramref name="isRemoved"/>
    /// says whether a referencing object goes with the set, so references between targets never
    /// block, whatever order the targets were given in. <paramref name="flattened"/> must hold the
    /// same instances as <paramref name="roots"/>.
    /// </summary>
    public static IReadOnlyList<string> ReferencingPaths(
        IReadOnlyList<ModelObject> flattened,
        IEnumerable<ModelObject> roots,
        Func<ModelObject, bool> isRemoved)
    {
        var targets = new List<ModelObject>();
        foreach (var root in roots)
        {
            if (IsDaxNamed(root.Kind))
                targets.Add(root);
            if (root.Kind == ModelObjectKind.Table)
                targets.AddRange(root.Children.Where(c => c.Kind is ModelObjectKind.Measure or ModelObjectKind.Column
                    or ModelObjectKind.CalculatedColumn or ModelObjectKind.Calendar));
        }

        if (targets.Count == 0)
            return [];

        var paths = new List<string>();
        foreach (var site in new DependencyGraph(flattened).SitesReferencingAny(targets))
        {
            if (isRemoved(site.Source))
                continue;
            if (!paths.Contains(site.Source.Path, StringComparer.OrdinalIgnoreCase))
                paths.Add(site.Source.Path);
        }

        return paths;
    }

    private static bool IsDaxNamed(ModelObjectKind kind)
        => kind is ModelObjectKind.Table or ModelObjectKind.Measure or ModelObjectKind.Column
            or ModelObjectKind.CalculatedColumn or ModelObjectKind.Function or ModelObjectKind.Calendar;

    public static string BlockedMessage(IReadOnlyList<string> references)
        => BlockedMessage("this object", references, []);

    /// <summary>The block message for DAX and Power Query (M) references to removed objects.</summary>
    public static string BlockedMessage(string subject, IReadOnlyList<string> dax, IReadOnlyList<string> m)
    {
        var parts = new List<string>();
        if (dax.Count > 0)
            parts.Add($"{dax.Count} DAX reference(s) in: {string.Join(", ", dax)}");
        if (m.Count > 0)
            parts.Add($"{m.Count} Power Query (M) reference(s) in: {string.Join(", ", m)}");
        return $"Removing {subject} breaks {string.Join("; and ", parts)}.";
    }

    private static bool IsWithin(string path, string tablePath)
        => path.StartsWith($"{tablePath}/", StringComparison.OrdinalIgnoreCase);
}
