using System.Globalization;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Provider.Tom;

/// <summary>How a rollback puts a checkpoint back (ADR 0003 §1).</summary>
internal enum TomCheckpointRestore
{
    /// <summary>Replace the live <see cref="Database"/> with a copy of the checkpoint. Exact,
    /// including sibling order. Only possible when nothing outside the session owns the
    /// database, so for TMDL and <c>.bim</c> sources.</summary>
    Swap,

    /// <summary>Copy the checkpoint's model onto the live one with <c>Model.CopyTo</c>. Same
    /// content, but re-created objects are appended, so sibling order can differ. For databases
    /// that belong to a <see cref="Microsoft.AnalysisServices.Tabular.Server"/>.</summary>
    CopyTo
}

/// <summary>
/// Records every TOM write made through <see cref="Writer"/> (ADR 0002 §2) and groups the
/// writes into transactions that commit into <see cref="ModelChange"/> lists or roll back by
/// restoring a checkpoint (ADR 0003). Transactions nest: an inner one is a savepoint whose
/// commit folds into the outer one and whose rollback undoes only its own writes.
/// </summary>
/// <remarks>
/// Not thread-safe: the session's lease gate serializes access. The <see cref="Database"/>
/// instance can change on rollback, so callers take it from here for each lease and never keep it.
/// </remarks>
internal sealed class TomChangeJournal
{
    private readonly TomCheckpointRestore _restore;
    private readonly List<TomJournalEntry> _entries = [];
    private readonly Stack<Frame> _frames = new();
    // A checkpoint of the model as it is now, taken ahead of the next write (#423). Any write
    // or restore makes it stale, so it is dropped then; a checkpoint is never mutated, so the
    // same one can serve as a transaction's start and an undo's redo step.
    private TomCheckpoint? _spare;

    public TomChangeJournal(Database database, TomCheckpointRestore? restore = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
        _restore = restore ?? (database.Server is null ? TomCheckpointRestore.Swap : TomCheckpointRestore.CopyTo);
        Writer = new TomWriter(this);
    }

    /// <summary>The live database. Replaced by a <see cref="TomCheckpointRestore.Swap"/> rollback.</summary>
    public Database Database { get; private set; }

    public TomObjectIdMap Ids { get; } = new();

    /// <summary>The writer that records into this journal.</summary>
    public TomWriter Writer { get; }

    /// <summary>How many transactions are open: 0 outside any, 1 in a top-level one, more in savepoints.</summary>
    public int Depth => _frames.Count;

    /// <summary>The entries recorded by the open transactions, oldest first.</summary>
    public IReadOnlyList<TomJournalEntry> Entries => _entries;

    /// <summary>Opens a transaction, or a savepoint inside the open one.</summary>
    public void Begin() => _frames.Push(new Frame(_entries.Count));

    /// <summary>
    /// Commits the innermost transaction. A savepoint's entries join the outer transaction and
    /// the result is empty. A top-level commit returns the transaction's changes, cascades
    /// included, and clears the entries.
    /// </summary>
    public IReadOnlyList<ModelChange> Commit() => Commit(out _);

    /// <inheritdoc cref="Commit()"/>
    /// <param name="before">For a top-level commit, the model as it was when the transaction
    /// began, or <c>null</c> when it wrote nothing; the session keeps it as the undo step
    /// (ADR 0003 §3). Always <c>null</c> for a savepoint.</param>
    public IReadOnlyList<ModelChange> Commit(out TomCheckpoint? before)
    {
        RequireTransaction();
        var frame = _frames.Pop();
        before = null;
        if (_frames.Count > 0)
            return [];

        var changes = TomChangeEvents.Build(_entries, Ids);
        _entries.Clear();
        before = frame.Checkpoint;
        return changes;
    }

    /// <summary>True when a checkpoint of the model as it is now is ready, so the next
    /// transaction's first write and the next <see cref="Capture"/> take no copy.</summary>
    public bool HasSpareCheckpoint => _spare is not null;

    /// <summary>A checkpoint of the model as it is now, outside any transaction: the redo step
    /// an undo keeps.</summary>
    public TomCheckpoint Capture()
    {
        RequireNoTransaction();
        return _spare ??= TakeCheckpoint();
    }

    /// <summary>Takes the checkpoint the next write would take, outside any transaction, so
    /// the write does not wait for it (#423). Does nothing when one is ready.</summary>
    public void PrepareCheckpoint()
    {
        RequireNoTransaction();
        _spare ??= TakeCheckpoint();
    }

    /// <summary>Drops the ready checkpoint: the model changed in a way the journal does not
    /// record, such as a save that updates it from the server.</summary>
    public void DiscardSpareCheckpoint() => _spare = null;

    /// <summary>Puts <paramref name="checkpoint"/> back outside any transaction: an undo or redo.
    /// The checkpoint stays unchanged, so it can be restored again.</summary>
    public void RestoreTo(TomCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireNoTransaction();
        Restore(checkpoint);
    }

    /// <summary>
    /// Replaces the model with <paramref name="database"/>, read again from the source, outside
    /// any transaction. An object whose kind and path the old model had keeps its ID; the rest get
    /// new ones.
    /// </summary>
    public void Reload(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        RequireNoTransaction();
        var byPath = new Dictionary<(ModelObjectKind, string), ObjectId>();
        foreach (var old in TomObjectTree.Walk(Database.Model))
            byPath.TryAdd((TomObjectTree.KindOf(old)!.Value, TomObjectTree.PathOf(old)), Ids.GetOrAdd(old));

        var ids = new Dictionary<MetadataObject, ObjectId>(ReferenceEqualityComparer.Instance);
        foreach (var loaded in TomObjectTree.Walk(database.Model))
        {
            if (byPath.TryGetValue((TomObjectTree.KindOf(loaded)!.Value, TomObjectTree.PathOf(loaded)), out var id))
                ids[loaded] = id;
        }

        Database = database;
        Ids.Reset(ids);
        _spare = null;
    }

    /// <summary>Rolls back the innermost transaction: restores the model and IDs to where they
    /// were when it began and drops its entries.</summary>
    public void Rollback()
    {
        RequireTransaction();
        var frame = _frames.Pop();
        if (frame.Checkpoint is { } checkpoint)
            Restore(checkpoint);

        _entries.RemoveRange(frame.EntryStart, _entries.Count - frame.EntryStart);
    }

    /// <summary>
    /// Called by <see cref="TomWriter"/> before a write. Returns the tracked object the write
    /// changes, or <c>null</c> when the target is not attached to the model yet (an object
    /// being built), in which case the write is not recorded. Takes the checkpoint on the
    /// first recorded write of each open transaction.
    /// </summary>
    internal MetadataObject? Prepare(object target)
    {
        if (TomObjectTree.OwnerOf(target) is not { } owner || !TomObjectTree.IsAttached(owner))
            return null;

        RequireTransaction();
        if (_frames.Peek().Checkpoint is null)
        {
            // Every frame without a checkpoint has seen no write since it began, so the current
            // state is its starting state and one copy serves them all. A restore never mutates
            // a checkpoint, so sharing it is safe.
            var checkpoint = _spare ?? TakeCheckpoint();
            foreach (var frame in _frames.Where(f => f.Checkpoint is null))
                frame.Checkpoint = checkpoint;
        }

        // The write about to run makes the spare stale.
        _spare = null;
        return owner;
    }

    internal void Record(TomJournalEntry entry) => _entries.Add(entry);

    /// <summary>Builds the entry fields that describe <paramref name="subject"/> now.</summary>
    internal TomJournalEntry Describe(TomJournalOperation operation, MetadataObject subject, string? property = null,
        object? before = null, object? after = null)
        => new(operation, subject, Ids.GetOrAdd(subject), TomObjectTree.KindOf(subject)!.Value,
            TomObjectTree.PathOf(subject), TomObjectTree.LineageTagOf(subject), property,
            Neutral(before), Neutral(after), AncestorIds(subject));

    private IReadOnlyList<ObjectId> AncestorIds(MetadataObject subject)
    {
        var ancestors = new List<ObjectId>();
        for (var current = subject.Parent; current is not null; current = current.Parent)
        {
            if (TomObjectTree.IsTracked(current))
                ancestors.Add(Ids.GetOrAdd(current));
        }

        return ancestors;
    }

    private void RequireTransaction()
    {
        if (_frames.Count == 0)
            throw new InvalidOperationException("TOM writes and commits need an open journal transaction.");
    }

    private void RequireNoTransaction()
    {
        if (_frames.Count > 0)
            throw new InvalidOperationException("Commit or roll back the open journal transaction first.");
    }

    private TomCheckpoint TakeCheckpoint()
    {
        var copy = Copy(Database);
        var ids = new Dictionary<MetadataObject, ObjectId>(ReferenceEqualityComparer.Instance);
        // Give every object its ID now: one that first gets an ID after this point would
        // otherwise come back from a restore without it and be given a new one.
        foreach (var (live, copied) in TomObjectIdMap.Pair(TomObjectTree.Walk(Database.Model), TomObjectTree.Walk(copy.Model)))
            ids[copied] = Ids.GetOrAdd(live);

        return new TomCheckpoint(copy, ids);
    }

    /// <summary><c>Database.Clone()</c>, plus what it leaves out: the compatibility mode, which
    /// TMDL writes as <c>compatibilityMode</c> (TOM 19.117).</summary>
    private static Database Copy(Database database)
    {
        var copy = database.Clone();
        if (copy.CompatibilityMode != database.CompatibilityMode)
            copy.CompatibilityMode = database.CompatibilityMode;
        return copy;
    }

    private void Restore(TomCheckpoint checkpoint)
    {
        var ids = new Dictionary<MetadataObject, ObjectId>(ReferenceEqualityComparer.Instance);
        if (_restore == TomCheckpointRestore.Swap)
        {
            // Restore a copy, not the checkpoint itself: an outer transaction may share it.
            var restored = Copy(checkpoint.Database);
            foreach (var (saved, live) in TomObjectIdMap.Pair(TomObjectTree.Walk(checkpoint.Database.Model), TomObjectTree.Walk(restored.Model)))
            {
                if (checkpoint.Ids.TryGetValue(saved, out var id))
                    ids[live] = id;
            }

            Database = restored;
        }
        else
        {
            checkpoint.Database.Model.CopyTo(Database.Model);
            if (Database.CompatibilityLevel != checkpoint.Database.CompatibilityLevel)
                Database.CompatibilityLevel = checkpoint.Database.CompatibilityLevel;

            // CopyTo keeps unchanged instances and re-creates the rest, so match by kind and path.
            var byPath = new Dictionary<(ModelObjectKind, string), ObjectId>();
            foreach (var saved in TomObjectTree.Walk(checkpoint.Database.Model))
            {
                if (checkpoint.Ids.TryGetValue(saved, out var id))
                    byPath.TryAdd((TomObjectTree.KindOf(saved)!.Value, TomObjectTree.PathOf(saved)), id);
            }

            foreach (var live in TomObjectTree.Walk(Database.Model))
            {
                if (byPath.TryGetValue((TomObjectTree.KindOf(live)!.Value, TomObjectTree.PathOf(live)), out var id))
                    ids[live] = id;
            }
        }

        Ids.Reset(ids);
        // The model now holds what the checkpoint holds, so the next write can start from it.
        _spare = checkpoint;
    }

    /// <summary>The provider-neutral text of a value: object references by path or name.</summary>
    private static string? Neutral(object? value) => value switch
    {
        null => null,
        string text => text,
        MetadataObject obj when TomObjectTree.IsTracked(obj) && TomObjectTree.IsAttached(obj) => TomObjectTree.PathOf(obj),
        NamedMetadataObject named => named.Name,
        MetadataObject obj => obj.ObjectType.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    /// <summary>The label a write to an untracked child carries in its owner's change.</summary>
    internal static string LabelOf(MetadataObject item) => item switch
    {
        Annotation annotation => $"{PropertyBagKeys.AnnotationPrefix}{annotation.Name}",
        ObjectTranslation translation =>
            $"{PropertyBagKeys.TranslationPrefix}{(translation.Parent as Culture)?.Name}/{translation.Property}",
        _ => item.GetType().Name
    };

    private sealed class Frame(int entryStart)
    {
        public int EntryStart { get; } = entryStart;

        public TomCheckpoint? Checkpoint { get; set; }
    }
}

/// <summary>A copy of the model and the ID of each of its objects (ADR 0003 §1). Never
/// mutated: a restore works on a copy of it.</summary>
internal sealed record TomCheckpoint(Database Database, Dictionary<MetadataObject, ObjectId> Ids);

internal enum TomJournalOperation
{
    Set,
    Attach,
    Detach,
    Rebind
}

/// <summary>
/// One recorded write. <see cref="Subject"/> is the live tracked object the write is about: the
/// owner of a property write, or the object attached or detached (its owner when the object is
/// untracked, such as an annotation). The remaining fields describe the write without TOM
/// references, so it can be replayed onto a reloaded model (#374): the subject's ID, kind, path
/// and LineageTag before the write, the property label and the before and after values as text.
/// </summary>
/// <param name="Property">For a <see cref="TomJournalOperation.Set"/>, the property name, or a
/// label such as <c>Annotation:Name</c> or <c>FormatStringDefinition.Expression</c> when the write
/// went to an untracked child. For an attach or detach of an untracked child, that child's label.
/// <c>null</c> when a tracked object itself is attached or detached.</param>
/// <param name="Ancestors">IDs of the subject's tracked ancestors when the entry was recorded,
/// nearest first.</param>
internal sealed record TomJournalEntry(
    TomJournalOperation Operation,
    MetadataObject Subject,
    ObjectId Id,
    ModelObjectKind Kind,
    string Path,
    string? LineageTag,
    string? Property,
    string? Before,
    string? After,
    IReadOnlyList<ObjectId> Ancestors);
