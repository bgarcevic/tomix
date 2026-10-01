using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Maps TOM instances to session-scoped <see cref="ObjectId"/>s (ADR 0001 §2). IDs are handed
/// out on first sight and never reused. TOM cannot re-attach a removed object, so an operation
/// that replaces an instance must <see cref="Rebind"/> the replacement before attaching it, and
/// restoring a checkpoint replaces the whole map (<see cref="Reset"/>).
/// </summary>
internal sealed class TomObjectIdMap
{
    private Dictionary<MetadataObject, ObjectId> _ids = new(ReferenceEqualityComparer.Instance);
    private long _last;

    /// <summary>The number of instances that currently hold an ID.</summary>
    public int Count => _ids.Count;

    /// <summary>The ID of <paramref name="obj"/>, assigning the next one on first sight.</summary>
    public ObjectId GetOrAdd(MetadataObject obj)
    {
        if (_ids.TryGetValue(obj, out var id))
            return id;

        id = new ObjectId(++_last);
        _ids.Add(obj, id);
        return id;
    }

    public bool TryGet(MetadataObject obj, out ObjectId id) => _ids.TryGetValue(obj, out id);

    /// <summary>
    /// Gives <paramref name="replacement"/>, and each tracked descendant, the ID of its
    /// counterpart under <paramref name="original"/>. The replacement must be a structural copy
    /// (such as <c>Clone()</c>) that does not hold IDs yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The two subtrees differ in shape, or the
    /// replacement already has an ID.</exception>
    public void Rebind(MetadataObject original, MetadataObject replacement)
    {
        foreach (var (from, to) in Pair(TomObjectTree.Subtree(original), TomObjectTree.Subtree(replacement)))
        {
            if (_ids.ContainsKey(to))
                throw new InvalidOperationException(
                    $"Cannot rebind the replacement for '{TomObjectTree.PathOf(from)}': it already has an ID. Rebind a replacement before attaching it.");

            _ids[to] = GetOrAdd(from);
        }
    }

    /// <summary>Replaces every mapping with <paramref name="ids"/>. The counter keeps going, so
    /// an ID handed out before is never handed out again.</summary>
    public void Reset(Dictionary<MetadataObject, ObjectId> ids)
    {
        foreach (var id in ids.Values)
            _last = Math.Max(_last, id.Value);

        _ids = ids;
    }

    /// <summary>Pairs two walks of structurally identical trees, checking each pair matches.</summary>
    internal static IEnumerable<(MetadataObject From, MetadataObject To)> Pair(
        IEnumerable<MetadataObject> from, IEnumerable<MetadataObject> to)
    {
        using var left = from.GetEnumerator();
        using var right = to.GetEnumerator();
        while (true)
        {
            var hasLeft = left.MoveNext();
            if (hasLeft != right.MoveNext())
                throw new InvalidOperationException("The two models differ in shape.");
            if (!hasLeft)
                yield break;
            if (left.Current.GetType() != right.Current.GetType())
                throw new InvalidOperationException(
                    $"The two models differ in shape at '{TomObjectTree.PathOf(left.Current)}'.");

            yield return (left.Current, right.Current);
        }
    }
}
