using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Folds a committed transaction's journal entries into the change list of a
/// <c>model.changed</c> event (ADR 0001 §7): at most one change per object, in the order
/// objects were first touched.
/// </summary>
/// <remarks>
/// Changes report what a client has to refetch, not every write:
/// <list type="bullet">
/// <item>An object added in the transaction is <c>added</c>, whatever was written to it after,
/// and its descendants are not listed. An object added and removed again is not listed.</item>
/// <item>A removed object is <c>removed</c> at the path it had before the transaction; objects
/// removed with an ancestor are not listed.</item>
/// <item>A name change is <c>renamed</c>; detaching and re-attaching under another parent (a
/// move, which TOM can only do as an instance swap plus rebind) is <c>moved</c>. Both carry
/// <c>oldPath</c>, and any other properties written. Descendants' paths follow implicitly.</item>
/// <item>Anything else is <c>modified</c>, naming the properties written. Writes to untracked
/// children (annotations, translations, perspective entries, expression definitions) count
/// against the tracked owner.</item>
/// </list>
/// </remarks>
internal static class TomChangeEvents
{
    public static IReadOnlyList<ModelChange> Build(IReadOnlyList<TomJournalEntry> entries, TomObjectIdMap ids)
    {
        var states = new Dictionary<ObjectId, State>();
        var order = new List<State>();
        foreach (var entry in entries)
        {
            if (!states.TryGetValue(entry.Id, out var state))
            {
                state = new State(entry);
                states.Add(entry.Id, state);
                order.Add(state);
            }

            state.Apply(entry);
        }

        var added = states.Values.Where(s => s.Added).Select(s => s.Id).ToHashSet();
        var removed = states.Values.Where(s => s.Removed).Select(s => s.Id).ToHashSet();

        var changes = new List<ModelChange>();
        foreach (var state in order)
        {
            if (state.ToChange(added, removed, ids) is { } change)
                changes.Add(change);
        }

        return changes;
    }

    private sealed class State(TomJournalEntry first)
    {
        private readonly List<string> _properties = [];
        private MetadataObject _subject = first.Subject;
        private IReadOnlyList<ObjectId> _ancestors = first.Ancestors;
        private IReadOnlyList<ObjectId> _removedAncestors = [];
        private bool _reattached;
        private bool _renamed;
        private bool _cancelled;

        public ObjectId Id { get; } = first.Id;

        public bool Added { get; private set; }

        public bool Removed { get; private set; }

        /// <summary>The path the object had before the transaction touched it.</summary>
        private string FirstPath { get; } = first.Path;

        private ModelObjectKind Kind { get; } = first.Kind;

        public void Apply(TomJournalEntry entry)
        {
            switch (entry.Operation)
            {
                case TomJournalOperation.Attach when entry.Property is null:
                    _subject = entry.Subject;
                    _ancestors = entry.Ancestors;
                    if (Removed)
                    {
                        Removed = false;
                        _reattached = true;
                    }
                    else
                    {
                        Added = true;
                    }

                    break;
                case TomJournalOperation.Detach when entry.Property is null:
                    if (Added)
                    {
                        Added = false;
                        _cancelled = true;
                    }
                    else
                    {
                        Removed = true;
                        _removedAncestors = entry.Ancestors;
                    }

                    break;
                case TomJournalOperation.Set when entry.Property == "Name":
                    _renamed = true;
                    break;
                case TomJournalOperation.Rebind:
                    break;
                default:
                    if (entry.Property is { } property && !_properties.Contains(property))
                        _properties.Add(property);
                    break;
            }
        }

        public ModelChange? ToChange(HashSet<ObjectId> added, HashSet<ObjectId> removed, TomObjectIdMap ids)
        {
            if (_cancelled && !Added)
                return null;

            if (Removed)
            {
                return _removedAncestors.Any(removed.Contains)
                    ? null
                    : new ModelChange(Id, Kind, ModelChangeKind.Removed, FirstPath);
            }

            // Gone with a removed ancestor, or written while being built under an added one.
            if (!TomObjectTree.IsAttached(_subject) || CurrentAncestors(ids).Any(added.Contains))
                return null;

            var path = TomObjectTree.PathOf(_subject);
            if (Added)
                return new ModelChange(Id, Kind, ModelChangeKind.Added, path);

            var properties = _properties.Count > 0 ? _properties.ToList() : null;
            if (_reattached && !string.Equals(path, FirstPath, StringComparison.Ordinal))
            {
                var parentChanged = _removedAncestors.FirstOrDefault() != _ancestors.FirstOrDefault();
                return new ModelChange(Id, Kind, parentChanged ? ModelChangeKind.Moved : ModelChangeKind.Renamed,
                    path, OldPath: FirstPath, Properties: properties);
            }

            if (_renamed && !string.Equals(path, FirstPath, StringComparison.Ordinal))
                return new ModelChange(Id, Kind, ModelChangeKind.Renamed, path, OldPath: FirstPath, Properties: properties);

            return properties is null && !_reattached
                ? null
                : new ModelChange(Id, Kind, ModelChangeKind.Modified, path, Properties: properties);
        }

        private IEnumerable<ObjectId> CurrentAncestors(TomObjectIdMap ids)
        {
            for (var current = _subject.Parent; current is not null; current = current.Parent)
            {
                if (TomObjectTree.IsTracked(current) && ids.TryGet(current, out var id))
                    yield return id;
            }
        }
    }
}
