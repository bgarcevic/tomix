using Tomix.App.Format.M;
using Tomix.App.M;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Mutations;

/// <summary>
/// The Power Query (M) impact of removing shared expressions: the objects whose M refers to one
/// by name — M partitions, other M shared expressions, and refresh policies' source and polling
/// queries (reported as their table). The bundled M parser reads each text, so field access
/// (<c>[Sales]</c>), record keys, and names the text binds itself (<c>let Sales = ...</c>,
/// function parameters) never count. A text the parser cannot read counts as referencing any
/// name it contains, so an unreadable query blocks rather than breaking silently.
/// </summary>
internal static class MReferenceGuard
{
    public static async Task<IReadOnlyList<string>> ReferencingPathsAsync(
        IReadOnlyList<ModelObject> flattened,
        IEnumerable<ModelObject> roots,
        Func<ModelObject, bool> isRemoved,
        CancellationToken cancellationToken)
    {
        var names = roots.Where(r => r.Kind == ModelObjectKind.Expression).Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
            return [];

        // Only texts that spell a removed name can refer to it; the rest never reach the engine.
        var candidates = flattened
            .Where(o => !isRemoved(o))
            .SelectMany(o => Texts(o).Where(text => names.Any(name => Mentions(text, name))).Select(text => (Object: o, Text: text)))
            .ToList();
        if (candidates.Count == 0)
            return [];

        var results = await PowerQueryEngine.Shared.ReferencesAsync(
            candidates.Select(c => c.Text).ToList(), cancellationToken);
        var paths = new List<string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var refers = results[i] is { Ok: true, Names: { } free } ? free.Any(names.Contains) : true;
            if (refers && !paths.Contains(candidates[i].Object.Path, StringComparer.OrdinalIgnoreCase))
                paths.Add(candidates[i].Object.Path);
        }

        return paths;
    }

    public static async Task<IReadOnlyList<string>> ReferencingPathsAsync(
        IModelSession session,
        string path,
        ModelObjectKind? type,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var matches = ModelObjects.ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), type)
            .Where(o => o.Kind == ModelObjectKind.Expression)
            .ToList();
        if (matches.Count != 1)
            return []; // not-found/ambiguous is the mutator's error to raise

        var target = matches[0];
        return await ReferencingPathsAsync(
            ModelObjects.ModelObjectProjection.Flatten(snapshot), [target], o => ReferenceEquals(o, target), cancellationToken);
    }

    /// <summary>The M texts an object holds.</summary>
    private static IEnumerable<string> Texts(ModelObject obj)
    {
        if (obj.Expression is { Length: > 0 } expression
            && (obj.Kind == ModelObjectKind.Partition && MExpressions.IsMExpression(obj.Kind, obj.Detail)
                || obj.Kind == ModelObjectKind.Expression && obj.Property(PropertyBagKeys.ExpressionKind) is null or "M"))
            yield return expression;

        if (obj.Kind == ModelObjectKind.Table)
        {
            if (obj.Property(PropertyBagKeys.RefreshPolicySourceExpression) is { Length: > 0 } source)
                yield return source;
            if (obj.Property(PropertyBagKeys.RefreshPolicyPollingExpression) is { Length: > 0 } polling)
                yield return polling;
        }
    }

    // A quoted identifier doubles the quotes in a name: #"Say ""hi""".
    private static bool Mentions(string text, string name)
        => text.Contains(name, StringComparison.Ordinal)
           || name.Contains('"') && text.Contains(name.Replace("\"", "\"\"", StringComparison.Ordinal), StringComparison.Ordinal);
}
