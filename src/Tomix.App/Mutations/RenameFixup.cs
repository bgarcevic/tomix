using System.Text;
using Tomix.App.Dax;
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
/// The DAX rewrites a rename requires, shared by <c>set -q name</c> and <c>mv</c>. Renaming a
/// table, measure, or column silently breaks DAX that references the old name; this plan holds
/// the splice-rewritten expressions (applied by default) plus the referencing objects whose DAX
/// cannot be written back (role RLS filters), which stay a warning — or fail under
/// <c>--strict-refs</c>. Rewriting by exact reference span preserves the author's formatting and
/// comments (the mechanism of Tabular Editor's <c>FormulaFixup</c>).
/// </summary>
internal sealed record RenameFixupPlan(
    IReadOnlyList<ModelExpressionEdit> Edits,
    IReadOnlyList<string> FixedPaths,
    IReadOnlyList<string> UnfixablePaths)
{
    public static readonly RenameFixupPlan Empty = new([], [], []);

    /// <summary>Every referencing object path, for the no-fixup warning.</summary>
    public IReadOnlyList<string> AllPaths => [.. FixedPaths, .. UnfixablePaths];
}

/// <summary>The fixup policy shared by <c>mv</c> and <c>set</c>.</summary>
internal static class RenameReferences
{
    /// <summary>
    /// Applies <paramref name="fixup"/> under the requested policy and returns the paths to
    /// report as broken. Default (<paramref name="fixRefs"/>): rewrite everything rewritable —
    /// only unfixable sites remain broken, and only they trip <paramref name="strictRefs"/>.
    /// With fixup off, every referencing path is broken and any of them trips strict.
    /// </summary>
    public static IReadOnlyList<string> Apply(
        IModelMutationSession mutator, RenameFixupPlan fixup, bool fixRefs, bool strictRefs)
    {
        if (!fixRefs)
        {
            var all = fixup.AllPaths;
            if (all.Count > 0 && strictRefs)
                throw new RenameBrokenReferencesException(RenameFixup.BrokenWarning(all));

            return all;
        }

        if (fixup.UnfixablePaths.Count > 0 && strictRefs)
            throw new RenameBrokenReferencesException(RenameFixup.UnfixableWarning(fixup.UnfixablePaths));

        if (fixup.Edits.Count > 0)
            mutator.RewriteExpressions(fixup.Edits);

        return fixup.UnfixablePaths;
    }
}

internal static class RenameFixup
{
    /// <summary>
    /// Kinds whose DAX properties can be written back through
    /// <see cref="IModelMutationSession.RewriteExpressions"/>. Role RLS filters are synthesized
    /// per-table in the snapshot and have no single writable property, so they stay warnings.
    /// </summary>
    private static bool IsFixable(ModelObjectKind kind)
        => kind is ModelObjectKind.Measure or ModelObjectKind.Column
            or ModelObjectKind.CalculationItem or ModelObjectKind.Partition;

    public static async Task<RenameFixupPlan> PlanAsync(
        IModelSession session,
        string path,
        ModelObjectKind? type,
        string newName,
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
            return RenameFixupPlan.Empty; // not-found/ambiguous is the mutator's error to raise

        var target = matches[0];

        // Case-only renames break nothing (DAX resolves names case-insensitively), and there is
        // no point rewriting references just to change their casing.
        if (string.Equals(target.Name, newName, StringComparison.OrdinalIgnoreCase))
            return RenameFixupPlan.Empty;

        var edits = new List<ModelExpressionEdit>();
        var fixedPaths = new List<string>();
        var unfixablePaths = new List<string>();

        foreach (var site in DependencyGraph.FromSnapshot(snapshot).SitesReferencing(target))
        {
            if (!IsFixable(site.Source.Kind))
            {
                if (!unfixablePaths.Contains(site.Source.Path))
                    unfixablePaths.Add(site.Source.Path);
                continue;
            }

            edits.Add(new ModelExpressionEdit(
                site.Source.Path,
                site.Source.Kind,
                site.Property,
                Rewrite(site, target, newName)));
            if (!fixedPaths.Contains(site.Source.Path))
                fixedPaths.Add(site.Source.Path);
        }

        return new RenameFixupPlan(edits, fixedPaths, unfixablePaths);
    }

    public static string BrokenWarning(IReadOnlyList<string> references)
        => $"Rename leaves {references.Count} broken DAX reference(s) in: {string.Join(", ", references)}. "
           + "Update them with 'tx replace' or inspect with 'tx deps'.";

    public static string UnfixableWarning(IReadOnlyList<string> references)
        => $"Rename breaks {references.Count} DAX reference(s) that cannot be rewritten automatically: "
           + $"{string.Join(", ", references)}. Update them manually with 'tx replace'.";

    /// <summary>
    /// Rebuilds one expression with every reference to <paramref name="target"/> replaced by its
    /// new-name form, splicing by span from last to first so earlier offsets stay valid.
    /// </summary>
    private static string Rewrite(ReferenceSite site, ModelObject target, string newName)
    {
        var text = new StringBuilder(site.Expression);
        foreach (var reference in site.References.OrderByDescending(r => r.Start))
        {
            var replacement = target.Kind == ModelObjectKind.Table
                ? reference.Object is { } child ? $"{QuoteTable(newName)}{Bracket(child)}" : QuoteTable(newName)
                : reference.FullyQualified ? $"{QuoteTable(TableOf(target))}{Bracket(newName)}" : Bracket(newName);

            text.Remove(reference.Start, reference.End - reference.Start + 1);
            text.Insert(reference.Start, replacement);
        }

        return text.ToString();
    }

    private static string TableOf(ModelObject target)
    {
        var slash = target.Path.IndexOf('/');
        return slash < 0 ? target.Path : target.Path[..slash];
    }

    // Rewritten references always come out quoted ('Table'[X]) — valid for every table name,
    // even where the original was written bare (Table[X]).
    private static string QuoteTable(string name)
        => $"'{name.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Bracket(string name)
        => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
}
