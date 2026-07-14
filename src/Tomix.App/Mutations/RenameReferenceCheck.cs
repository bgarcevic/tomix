using Tomix.App.Deps;
using Tomix.App.ModelObjects;
using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>
/// Thrown under <c>--strict-refs</c> when a rename would leave DAX references pointing at the
/// old name. Mapped to <c>TOMIX_RENAME_BREAKS_REFS</c> by <see cref="MutationRunner"/>.
/// </summary>
public sealed class RenameBrokenReferencesException : Exception
{
    public RenameBrokenReferencesException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Pre-rename reference check shared by <c>set -q name</c> and <c>mv</c>: renaming a table,
/// measure, or column does not rewrite DAX that references the old name, so downstream
/// expressions break silently (the local save succeeds; only a deploy surfaces the damage).
/// This finds the referencing objects before the rename so handlers can warn — or fail under
/// <c>--strict-refs</c> — while the model is still intact.
/// </summary>
internal static class RenameReferenceCheck
{
    public static async Task<IReadOnlyList<string>> FindReferencingPathsAsync(
        IModelSession session,
        string path,
        ModelObjectKind? type,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // The mutator accepts DAX bracket paths; the snapshot lookup does not — convert first.
        // Only DAX-named kinds can leave text references behind, so filter to them BEFORE the
        // uniqueness test: a partition sharing its table's name (the Desktop default) would
        // otherwise make every measure path look ambiguous and silently skip the check.
        var matches = ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), type)
            .Where(o => o.Kind is ModelObjectKind.Table or ModelObjectKind.Measure or ModelObjectKind.Column)
            .ToList();
        if (matches.Count != 1)
            return []; // not-found/ambiguous is the mutator's error to raise, with its own codes

        var target = matches[0];

        return DependencyGraph.FromSnapshot(snapshot)
            .DirectDownstream(target)
            .Select(d => d.Path)
            .ToList();
    }

    public static string Warning(IReadOnlyList<string> references)
        => $"Rename leaves {references.Count} broken DAX reference(s) in: {string.Join(", ", references)}. "
           + "Update them with 'tx replace' or inspect with 'tx deps'.";
}
