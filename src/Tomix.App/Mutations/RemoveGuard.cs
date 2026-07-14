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
            .Where(o => o.Kind is ModelObjectKind.Table or ModelObjectKind.Measure or ModelObjectKind.Column)
            .ToList();
        if (matches.Count != 1)
            return []; // not-found/ambiguous is the mutator's error to raise

        var target = matches[0];
        var flattened = ModelObjectProjection.Flatten(snapshot);

        // Deleting a table deletes its measures and columns with it: references to any of them
        // break, while references from inside the table vanish along with it.
        var targets = new List<ModelObject> { target };
        if (target.Kind == ModelObjectKind.Table)
            targets.AddRange(flattened.Where(o =>
                o.Kind is ModelObjectKind.Measure or ModelObjectKind.Column
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

    public static string BlockedMessage(IReadOnlyList<string> references)
        => $"Removing this object breaks {references.Count} DAX reference(s) in: "
           + $"{string.Join(", ", references)}.";

    private static bool IsWithin(string path, string tablePath)
        => path.StartsWith($"{tablePath}/", StringComparison.OrdinalIgnoreCase);
}
