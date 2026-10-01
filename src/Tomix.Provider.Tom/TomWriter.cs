using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom;

/// <summary>
/// The only way the mutation collaborators write to TOM (ADR 0002 §2): property writes, collection
/// adds and removes, and instance replacements. A writer bound to a <see cref="TomChangeJournal"/>
/// records each write to an attached object; <see cref="Untracked"/> just writes, which is what
/// one-shot sessions use. Writes to objects not attached yet (being built) are never recorded:
/// they surface as part of the add that attaches them.
/// </summary>
internal sealed class TomWriter
{
    private const BindingFlags Reflect = BindingFlags.DoNotWrapExceptions;

    private static readonly ConcurrentDictionary<Type, PropertyInfo> CollectionParents = new();

    private readonly TomChangeJournal? _journal;

    internal TomWriter(TomChangeJournal? journal) => _journal = journal;

    /// <summary>A writer that records nothing.</summary>
    public static TomWriter Untracked { get; } = new(null);

    /// <summary>
    /// <c>target.Property = value</c>, for example <c>w.Set(measure, m => m.Expression, text)</c>.
    /// Writing the value a property already has does nothing. Setting a contained child
    /// (<c>Measure.KPI</c>, <c>Table.RefreshPolicy</c>) records it as attached and the old one
    /// as detached.
    /// </summary>
    public void Set<TTarget, TValue>(TTarget target, Expression<Func<TTarget, TValue>> property, TValue value)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(target);
        var info = PropertyOf(property);
        var before = (TValue)info.GetValue(target, Reflect, null, null, null)!;
        if (EqualityComparer<TValue>.Default.Equals(before, value))
            return;

        if (_journal?.Prepare(target) is not { } owner)
        {
            info.SetValue(target, value, Reflect, null, null, null);
            return;
        }

        var self = ReferenceEquals(target, owner) || target is Database;
        var label = self ? info.Name : $"{LabelOf(target)}.{info.Name}";
        var setEntry = _journal.Describe(TomJournalOperation.Set, owner, label, before, value);
        var detached = before is MetadataObject old && ReferenceEquals(old.Parent, target) && TomObjectTree.IsTracked(old)
            ? _journal.Describe(TomJournalOperation.Detach, old)
            : null;

        info.SetValue(target, value, Reflect, null, null, null);

        if (detached is not null)
            _journal.Record(detached);
        _journal.Record(setEntry);
        if (value is MetadataObject added && ReferenceEquals(added.Parent, target) && TomObjectTree.IsTracked(added))
            _journal.Record(_journal.Describe(TomJournalOperation.Attach, added));
    }

    /// <summary><c>collection.Add(item)</c> for a TOM collection.</summary>
    public void Attach<T>(ICollection<T> collection, T item) where T : MetadataObject
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(item);
        if (_journal?.Prepare(ParentOf(collection)) is null)
        {
            collection.Add(item);
            return;
        }

        collection.Add(item);
        _journal.Record(TomObjectTree.IsTracked(item)
            ? _journal.Describe(TomJournalOperation.Attach, item)
            : _journal.Describe(TomJournalOperation.Attach, TomObjectTree.OwnerOf(item)!, TomChangeJournal.LabelOf(item)));
    }

    /// <summary><c>collection.Remove(item)</c> for a TOM collection.</summary>
    /// <returns>Whether the item was in the collection.</returns>
    public bool Detach<T>(ICollection<T> collection, T item) where T : MetadataObject
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(item);
        if (!collection.Contains(item))
            return false;

        if (_journal?.Prepare(ParentOf(collection)) is null)
            return collection.Remove(item);

        // Describe before removing: the path and the owner come from the parent chain.
        var entry = TomObjectTree.IsTracked(item)
            ? _journal.Describe(TomJournalOperation.Detach, item)
            : _journal.Describe(TomJournalOperation.Detach, TomObjectTree.OwnerOf(item)!, TomChangeJournal.LabelOf(item));
        collection.Remove(item);
        _journal.Record(entry);
        return true;
    }

    /// <summary>
    /// Gives <paramref name="replacement"/> (a <c>Clone()</c> of <paramref name="original"/>, or an
    /// equivalent copy) the ID of <paramref name="original"/>, and the same for their tracked
    /// descendants. Call it before attaching the replacement, wherever TOM forces an instance
    /// swap because it cannot re-attach a removed object.
    /// </summary>
    public void Rebind(MetadataObject original, MetadataObject replacement)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (_journal is null || !TomObjectTree.IsTracked(original))
            return;

        _journal.Ids.Rebind(original, replacement);
        if (TomObjectTree.IsAttached(original))
            _journal.Record(_journal.Describe(TomJournalOperation.Rebind, original));
    }

    private static string LabelOf(object target)
        => target is MetadataObject obj ? TomChangeJournal.LabelOf(obj) : target.GetType().Name;

    private static MetadataObject ParentOf<T>(ICollection<T> collection)
        => (MetadataObject)CollectionParents
               .GetOrAdd(collection.GetType(), type => type.GetProperty("Parent")
                   ?? throw new ArgumentException($"{type.Name} is not a TOM collection.", nameof(collection)))
               .GetValue(collection, Reflect, null, null, null)!;

    private static PropertyInfo PropertyOf<TTarget, TValue>(Expression<Func<TTarget, TValue>> property)
        => property.Body is MemberExpression { Member: PropertyInfo info } member && member.Expression is ParameterExpression
            ? info
            : throw new ArgumentException($"'{property}' must select a property of its parameter, as in x => x.Name.", nameof(property));
}
