using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// A TOM model held open for many requests (ADR 0001), for TMDL, <c>.bim</c> and XMLA sources.
/// All access to the <see cref="Database"/> goes through a FIFO <see cref="LeaseGate"/>; a lease
/// is a <see cref="TomChangeJournal"/> transaction and the only way to reach the model
/// (ADR 0002 §1). Change events come from the journal; rollback, undo and redo restore its
/// checkpoints (ADR 0003).
/// </summary>
/// <remarks>
/// Each committed transaction that changed something is one undo step: the checkpoint taken
/// before it, kept with the changes it made. A file source is watched: when its content differs
/// from what the session last opened, reloaded or saved, the session is
/// <see cref="SessionState.Stale"/> and an in-place save fails until the caller reloads or keeps
/// its own changes (#351).
/// </remarks>
public sealed class TomLiveModelSession : ILiveModelSession
{
    /// <summary>Undo steps kept by default. Each holds a full copy of the model (ADR 0003 §3),
    /// so memory grows with model size times this number.</summary>
    public const int DefaultUndoLimit = 50;

    /// <summary>How long an explicit transaction may sit with no lease in it before it is
    /// rolled back, so a client that went away cannot hold the model forever.</summary>
    public static readonly TimeSpan DefaultTransactionIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How long the source must stay quiet after a change before the session checks it,
    /// so a checkout or a save that writes many files is checked once.</summary>
    internal static readonly TimeSpan SourceSettleDelay = TimeSpan.FromMilliseconds(250);

    private readonly TomModelSource _source;
    private readonly LeaseGate _gate = new();
    private readonly AsyncLocal<LeaseFlow?> _flow = new();
    private readonly object _sync = new();
    private readonly LinkedList<UndoStep> _undo = new();
    private readonly Stack<UndoStep> _redo = new();
    private long _version;
    // Identifies the model's content: a new token per committed change, the step's own token on
    // undo and redo. The session is clean while the saved token is the current one.
    private object _content = new();
    private object? _savedContent;
    private Lease? _explicit;
    private Timer? _idleTimer;
    private bool _saving;
    private bool _closed;
    private int _closing;
    private int _transactions;
    private SessionState _state = SessionState.Clean;
    private LiveModelSnapshot? _snapshot;
    // The source as the session last opened, reloaded or saved it, and whether it now differs.
    // Guarded by _sourceLock, so a check never reads files a save is writing.
    private readonly SemaphoreSlim _sourceLock = new(1, 1);
    private string? _baseline;
    private bool _sourceChanged;
    private readonly IDisposable? _watch;
    private readonly Timer _settle;

    internal TomLiveModelSession(TomModelSource source)
    {
        _source = source;
        _savedContent = _content;
        _baseline = source.Fingerprint();
        Journal = new TomChangeJournal(source.Load(), source.Restore);
        _settle = new Timer(_ => _ = CheckQuietlyAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watch = source.Watch(() => _settle.Change(SourceSettleDelay, Timeout.InfiniteTimeSpan));
    }

    /// <summary>Opens a live session over a TMDL folder (the <c>definition</c> folder itself).</summary>
    /// <exception cref="ModelLoadException">The folder cannot be read as TMDL.</exception>
    public static ILiveModelSession OpenTmdlFolder(ModelReference reference, string folder, IAccessTokenProvider? tokenProvider = null)
        => new TomLiveModelSession(TomModelSource.File(reference, folder, "tmdl", LoadTmdlFolder, tokenProvider));

    internal TomChangeJournal Journal { get; }

    internal TomModelSource Source => _source;

    /// <summary>How many undo steps to keep; the oldest is dropped past it.</summary>
    internal int UndoLimit { get; init; } = DefaultUndoLimit;

    /// <summary>Idle time after which an explicit transaction is rolled back;
    /// <see cref="Timeout.InfiniteTimeSpan"/> never rolls it back.</summary>
    internal TimeSpan TransactionIdleTimeout { get; init; } = DefaultTransactionIdleTimeout;

    public ModelReference Reference => _source.Reference;

    public string SourcePath => _source.SourcePath;

    public SessionState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsDirty
    {
        get { lock (_sync) return !ReferenceEquals(_savedContent, _content); }
    }

    public long Version
    {
        get { lock (_sync) return _version; }
    }

    public bool CanUndo
    {
        get { lock (_sync) return _undo.Count > 0; }
    }

    public bool CanRedo
    {
        get { lock (_sync) return _redo.Count > 0; }
    }

    public IReadOnlyList<LiveHistoryStep> History
    {
        get
        {
            lock (_sync)
                return [.. _undo.Select(step => step.ToHistory(undone: false)), .. _redo.Select(step => step.ToHistory(undone: true))];
        }
    }

    public bool CanReload => _source.CanReload;

    /// <summary>True when the source changed outside the session since it was opened, reloaded or saved.</summary>
    public bool SourceChanged
    {
        get { lock (_sync) return _sourceChanged; }
    }

    public event EventHandler<ModelChangeBatch>? Changed;

    public event EventHandler<SessionStateChange>? StateChanged;

    public Task<ILiveSessionLease> LeaseAsync(LiveLeaseOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfClosed();

        // Not async on purpose: the flow is set in the caller's execution context, so code the
        // caller runs while holding the lease finds it and joins instead of deadlocking. Tasks
        // the caller starts while holding it inherit that context and join too; a host serving
        // several clients starts each request from a context that holds no lease.
        if (_flow.Value?.Current is { } outer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ILiveSessionLease>(Join(outer, options));
        }

        var flow = new LeaseFlow();
        _flow.Value = flow;
        return OpenTransactionOf(options.Client) is { } open
            ? JoinTransactionAsync(flow, open, options, cancellationToken)
            : AcquireAsync(flow, options, isExplicit: false, cancellationToken);
    }

    /// <remarks>The transaction is not tied to the caller's asynchronous flow: the client's
    /// later leases join it by <see cref="LiveLeaseOptions.Client"/>, one at a time, each as a
    /// savepoint. It is rolled back after <see cref="TransactionIdleTimeout"/> with no lease in it.</remarks>
    /// <exception cref="InvalidOperationException">The caller holds a lease, or the client
    /// already has an open transaction.</exception>
    public async Task<ILiveSessionLease> BeginTransactionAsync(LiveLeaseOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfClosed();
        if (_flow.Value?.Current is not null || OpenTransactionOf(options.Client) is not null)
            throw new InvalidOperationException("A transaction is already open; transactions do not nest.");

        return await AcquireAsync(new LeaseFlow(), options, isExplicit: true, cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="InvalidOperationException">The caller holds a lease, or the client has
    /// an open transaction: commit or roll it back first.</exception>
    public Task<ModelChangeBatch?> UndoAsync(string? client, CancellationToken cancellationToken)
        => StepAsync(client, ChangeOriginKind.Undo, cancellationToken);

    /// <inheritdoc cref="UndoAsync"/>
    public Task<ModelChangeBatch?> RedoAsync(string? client, CancellationToken cancellationToken)
        => StepAsync(client, ChangeOriginKind.Redo, cancellationToken);

    public async Task<bool> CheckSourceAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        await _sourceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            changed = _baseline is not null && _source.Fingerprint() != _baseline;
            lock (_sync)
                _sourceChanged = changed;
        }
        finally
        {
            _sourceLock.Release();
        }

        UpdateState();
        return changed;
    }

    /// <exception cref="ModelLoadException">The source cannot be read; the session is left as it was.</exception>
    public async Task<ModelChangeBatch> ReloadAsync(string? client, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (!_source.CanReload)
            throw new NotSupportedException("Only a session on a TMDL folder or a model file can reload its source.");
        if (_flow.Value?.Current is not null)
            throw new InvalidOperationException("Reload is not available while holding a lease; end the lease first.");
        if (OpenTransactionOf(client) is not null)
            throw new InvalidOperationException("Reload is not available inside a transaction; commit or roll it back first.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            ModelChangeBatch batch;
            await _sourceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Fingerprint first: a change landing while the model loads is then seen as one.
                var baseline = _source.Fingerprint();
                var database = _source.Load();
                var before = Snapshot();
                Journal.Reload(database);
                var changes = ReloadChanges(before, Snapshot(), Journal.Ids.GetOrAdd(database.Model));
                lock (_sync)
                {
                    _baseline = baseline;
                    _sourceChanged = false;
                    _undo.Clear();
                    _redo.Clear();
                    _content = new object();
                    _savedContent = _content;
                    batch = new ModelChangeBatch(++_version, $"t{++_transactions}", new ChangeOrigin(client, ChangeOriginKind.Reload), changes);
                }
            }
            finally
            {
                _sourceLock.Release();
            }

            Changed?.Invoke(this, batch);
            UpdateState();
            return batch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LiveModelSnapshot> GetLiveSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _snapshot);
        if (cached is not null && cached.Version == Version)
            return cached;

        return await ReadAsync(CacheSnapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => (await GetLiveSnapshotAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
        => ReadAsync(() => _source.Summarize(Journal.Database), cancellationToken);

    /// <summary>Closes the session once the current lease ends, rolling back an open explicit
    /// transaction. Leases still waiting fail with <see cref="ObjectDisposedException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_flow.Value?.Current is not null)
            throw new InvalidOperationException("A live session cannot be closed from inside one of its leases.");
        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return;

        _watch?.Dispose();
        await _settle.DisposeAsync().ConfigureAwait(false);

        Lease? open;
        lock (_sync)
            open = _explicit;
        if (open is not null)
            await open.DisposeAsync().ConfigureAwait(false);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _closed = true;
                _undo.Clear();
                _redo.Clear();
            }

            UpdateState();
            await _source.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The snapshot of the model as it is now, with session IDs. Callers hold the gate.</summary>
    internal ModelSnapshot Snapshot()
        => TomModelSummarizer.Snapshot(Journal.Database, _source.ModelName(Journal.Database), Journal.Ids);

    /// <summary>Saves through <paramref name="lease"/>. An in-place save becomes the save point
    /// once the lease's transaction commits with nothing written after the save.</summary>
    internal async Task<ModelExportResult> SaveAsync(Lease lease, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
    {
        lock (_sync)
            _saving = true;
        UpdateState();
        try
        {
            if (!_source.IsInPlace(outputPath))
                return await _source.SaveAsync(Journal.Database, outputPath, serialization, overwrite, cancellationToken).ConfigureAwait(false);

            await _sourceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_baseline is not null && _source.Fingerprint() != _baseline)
                {
                    lock (_sync)
                        _sourceChanged = true;
                    throw new ModelSourceChangedException(SourcePath);
                }

                var result = await _source.SaveAsync(Journal.Database, outputPath, serialization, overwrite, cancellationToken).ConfigureAwait(false);
                lease.Root.EntriesAtSave = Journal.Entries.Count;
                _baseline = _source.Fingerprint();
                lock (_sync)
                    _sourceChanged = false;
                return result;
            }
            finally
            {
                _sourceLock.Release();
            }
        }
        finally
        {
            lock (_sync)
                _saving = false;
            UpdateState();
        }
    }

    /// <summary>Takes the source as it is now as the state the next save replaces
    /// (<see cref="IExternalChangeSession.KeepChanges"/>). Callers hold a lease.</summary>
    internal void KeepChanges()
    {
        _sourceLock.Wait();
        try
        {
            _baseline = _source.Fingerprint();
            lock (_sync)
                _sourceChanged = false;
        }
        finally
        {
            _sourceLock.Release();
        }

        UpdateState();
    }

    /// <summary>A check the watcher starts: it reports through <see cref="StateChanged"/> and never throws.</summary>
    private async Task CheckQuietlyAsync()
    {
        try
        {
            await CheckSourceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed while the source settled.
        }
    }

    /// <summary>
    /// What a reload changed, from the snapshots before and after: objects only one side has are
    /// added or removed (their descendants are not listed again), and objects whose snapshot
    /// differs are modified, without property names.
    /// </summary>
    internal static IReadOnlyList<ModelChange> ReloadChanges(ModelSnapshot before, ModelSnapshot after, ObjectId model)
    {
        var changes = new List<ModelChange>();
        if (before.Name != after.Name || before.CompatibilityLevel != after.CompatibilityLevel
            || before.Description != after.Description || !SameProperties(before.Properties, after.Properties))
            changes.Add(new ModelChange(model, ModelObjectKind.Model, ModelChangeKind.Modified, TomObjectTree.ModelPath));

        Compare(before.Objects, after.Objects, changes);
        return changes;
    }

    private static void Compare(IReadOnlyList<ModelObject> before, IReadOnlyList<ModelObject> after, List<ModelChange> changes)
    {
        var old = new Dictionary<(ModelObjectKind, string), ModelObject>();
        foreach (var item in before)
            old.TryAdd((item.Kind, item.Path), item);

        var seen = new HashSet<(ModelObjectKind, string)>();
        foreach (var item in after)
        {
            var key = (item.Kind, item.Path);
            seen.Add(key);
            if (!old.TryGetValue(key, out var previous))
            {
                if (item.Id is { } added)
                    changes.Add(new ModelChange(added, item.Kind, ModelChangeKind.Added, item.Path));
                continue;
            }

            if (item.Id is { } id && !SameObject(previous, item))
                changes.Add(new ModelChange(id, item.Kind, ModelChangeKind.Modified, item.Path));
            Compare(previous.Children, item.Children, changes);
        }

        foreach (var item in before)
        {
            if (!seen.Contains((item.Kind, item.Path)) && item.Id is { } removed)
                changes.Add(new ModelChange(removed, item.Kind, ModelChangeKind.Removed, item.Path));
        }
    }

    private static bool SameObject(ModelObject a, ModelObject b)
        => a.Name == b.Name && a.Detail == b.Detail && a.Expression == b.Expression && a.Description == b.Description
           && a.Hidden == b.Hidden && a.SourceColumn == b.SourceColumn && SameProperties(a.Properties, b.Properties);

    private static bool SameProperties(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null)
            return a is null == b is null;
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    /// <summary>The published snapshot of the current version, built if needed. Callers hold the gate.</summary>
    private LiveModelSnapshot CacheSnapshot()
    {
        if (_snapshot is null || _snapshot.Version != _version)
            Volatile.Write(ref _snapshot, new LiveModelSnapshot(_version, Snapshot()));
        return _snapshot!;
    }

    private async Task<ILiveSessionLease> AcquireAsync(LeaseFlow flow, LiveLeaseOptions options, bool isExplicit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Lease lease;
        try
        {
            ThrowIfClosed();
            if (isExplicit)
            {
                if (OpenTransactionOf(options.Client) is not null)
                    throw new InvalidOperationException("A transaction is already open; transactions do not nest.");

                // Other clients read the last committed version while the transaction is open;
                // cache it now, because building it later would read uncommitted changes.
                CacheSnapshot();
            }

            Journal.Begin();
            lease = new Lease(this, flow, parent: null, options, $"t{++_transactions}", Version, isExplicit);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        flow.Current = lease;
        if (isExplicit)
        {
            lock (_sync)
                _explicit = lease;
            ArmIdleTimer(lease);
        }

        return lease;
    }

    private Lease Join(Lease outer, LiveLeaseOptions options)
    {
        Journal.Begin();
        var lease = new Lease(this, outer.Flow, outer, options, outer.Transaction, Version);
        outer.Flow.Current = lease;
        return lease;
    }

    /// <summary>The open explicit transaction, if <paramref name="client"/> owns it.</summary>
    private Lease? OpenTransactionOf(string? client)
    {
        lock (_sync)
            return _explicit is { } open && string.Equals(open.Options.Client, client, StringComparison.Ordinal) ? open : null;
    }

    /// <summary>A lease of the client that owns <paramref name="open"/>: waits for the client's
    /// previous lease in it to end, then runs as a savepoint inside the transaction.</summary>
    private async Task<ILiveSessionLease> JoinTransactionAsync(LeaseFlow flow, Lease open, LiveLeaseOptions options, CancellationToken cancellationToken)
    {
        if (!await open.TakeTurnAsync(cancellationToken).ConfigureAwait(false))
        {
            // The transaction ended while this lease waited: lease on its own.
            return await AcquireAsync(flow, options, isExplicit: false, cancellationToken).ConfigureAwait(false);
        }

        DisarmIdleTimer();
        Journal.Begin();
        var lease = new Lease(this, flow, open, options, open.Transaction, Version);
        flow.Current = lease;
        return lease;
    }

    private async Task<ModelChangeBatch?> CommitAsync(Lease lease, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(lease.Flow.Current, lease))
            throw new InvalidOperationException("A lease opened inside this one is still open; end it first.");

        if (lease.IsJoined)
        {
            Journal.Commit();
            EndJoined(lease);
            return null;
        }

        // An explicit transaction ends only between the leases that join it.
        if (lease.IsExplicit && !await lease.TakeTurnAsync(cancellationToken).ConfigureAwait(false))
            throw new ObjectDisposedException(nameof(Lease), "The transaction has already ended.");

        try
        {
            var entries = Journal.Entries.Count;
            var changes = Journal.Commit(out var before);
            lease.End();

            ModelChangeBatch? batch = null;
            lock (_sync)
            {
                if (changes.Count > 0)
                {
                    _version++;
                    batch = new ModelChangeBatch(_version, lease.Transaction,
                        new ChangeOrigin(lease.Options.Client, ChangeOriginKind.Apply), changes);
                    var previous = _content;
                    _content = new object();
                    _undo.AddLast(new UndoStep(before!, previous, _content, changes, lease.Transaction, lease.Options));
                    while (_undo.Count > UndoLimit)
                        _undo.RemoveFirst();
                    _redo.Clear();
                }

                if (lease.EntriesAtSave is { } atSave)
                    _savedContent = atSave == entries ? _content : null;
            }

            if (batch is not null)
                Changed?.Invoke(this, batch);
            UpdateState();
            return batch;
        }
        finally
        {
            ReleaseRoot(lease);
        }
    }

    private async Task RollbackAsync(Lease lease, CancellationToken cancellationToken)
    {
        if (lease.IsExplicit && !await lease.TakeTurnAsync(cancellationToken).ConfigureAwait(false))
            throw new ObjectDisposedException(nameof(Lease), "The transaction has already ended.");
        Rollback(lease);
    }

    private void Rollback(Lease lease)
    {
        // Disposal unwinds outside-in when a request fails; end the leases it opened first.
        while (lease.Flow.Current is { } inner && !ReferenceEquals(inner, lease))
            RollbackOne(inner);
        RollbackOne(lease);
    }

    private void RollbackOne(Lease lease)
    {
        if (lease.IsJoined)
        {
            try
            {
                Journal.Rollback();
            }
            finally
            {
                EndJoined(lease);
            }

            return;
        }

        try
        {
            Journal.Rollback();
            lock (_sync)
            {
                // A save inside a rolled-back transaction wrote state the model no longer has,
                // unless nothing had been written before it.
                if (lease.EntriesAtSave is { } atSave)
                    _savedContent = atSave == 0 ? _content : null;
            }

            UpdateState();
        }
        finally
        {
            lease.End();
            ReleaseRoot(lease);
        }
    }

    /// <summary>Ends a savepoint lease. One that joined an explicit transaction from its own
    /// flow hands the transaction to the client's next lease.</summary>
    private void EndJoined(Lease lease)
    {
        lease.End();
        if (lease.HoldsTurn)
        {
            ArmIdleTimer(lease.Parent!);
            lease.Parent!.Turns!.Release();
        }
    }

    private void ReleaseRoot(Lease lease)
    {
        if (lease.IsExplicit)
        {
            DisarmIdleTimer();
            lock (_sync)
            {
                if (ReferenceEquals(_explicit, lease))
                    _explicit = null;
            }

            // Leases queued for the transaction find it ended and lease on their own.
            lease.Turns!.Release();
        }

        _gate.Release();
    }

    private async Task<ModelChangeBatch?> StepAsync(string? client, ChangeOriginKind kind, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var name = kind == ChangeOriginKind.Undo ? "Undo" : "Redo";
        if (_flow.Value?.Current is not null)
            throw new InvalidOperationException($"{name} is not available while holding a lease; end the lease first.");
        if (OpenTransactionOf(client) is not null)
            throw new InvalidOperationException($"{name} is not available inside a transaction; commit or roll it back first.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            UndoStep? step;
            lock (_sync)
                step = kind == ChangeOriginKind.Undo ? _undo.Last?.Value : _redo.TryPeek(out var next) ? next : null;
            if (step is null)
                return null;

            ModelChangeBatch batch;
            if (kind == ChangeOriginKind.Undo)
            {
                // The model is still in the step's after state: keep it for a redo.
                step.After ??= Journal.Capture();
                Journal.RestoreTo(step.Before);
                lock (_sync)
                {
                    _undo.RemoveLast();
                    _redo.Push(step);
                    _content = step.BeforeContent;
                    batch = new ModelChangeBatch(++_version, $"t{++_transactions}", new ChangeOrigin(client, kind), Invert(step.Changes));
                }
            }
            else
            {
                Journal.RestoreTo(step.After!);
                lock (_sync)
                {
                    _redo.Pop();
                    _undo.AddLast(step);
                    _content = step.AfterContent;
                    batch = new ModelChangeBatch(++_version, $"t{++_transactions}", new ChangeOrigin(client, kind), step.Changes);
                }
            }

            Changed?.Invoke(this, batch);
            UpdateState();
            return batch;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The changes that take the model back across <paramref name="changes"/>.</summary>
    internal static IReadOnlyList<ModelChange> Invert(IReadOnlyList<ModelChange> changes)
        => changes.Reverse().Select(change => change.Change switch
        {
            ModelChangeKind.Added => change with { Change = ModelChangeKind.Removed },
            ModelChangeKind.Removed => change with { Change = ModelChangeKind.Added },
            ModelChangeKind.Renamed or ModelChangeKind.Moved when change.OldPath is { } old
                => change with { Path = old, OldPath = change.Path },
            _ => change
        }).ToList();

    private void ArmIdleTimer(Lease open)
    {
        if (TransactionIdleTimeout == Timeout.InfiniteTimeSpan)
            return;

        lock (_sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => _ = open.DisposeAsync().AsTask(), null, TransactionIdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmIdleTimer()
    {
        lock (_sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private async Task<T> ReadAsync<T>(Func<T> read, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_flow.Value?.Current is not null)
            throw new InvalidOperationException("Read the model through the lease's Session while holding a lease.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            return read();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateState()
    {
        SessionStateChange? change = null;
        lock (_sync)
        {
            var next = _closed ? SessionState.Closed
                : _saving ? SessionState.Saving
                : _sourceChanged ? SessionState.Stale
                : !ReferenceEquals(_savedContent, _content) ? SessionState.Dirty
                : SessionState.Clean;
            if (next != _state)
            {
                change = new SessionStateChange(_state, next, _version);
                _state = next;
            }
        }

        if (change is not null)
            StateChanged?.Invoke(this, change);
    }

    private void ThrowIfClosed()
    {
        lock (_sync)
            ObjectDisposedException.ThrowIf(_closed, this);
    }

    private static Database LoadTmdlFolder(string folder)
    {
        try
        {
            return TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        }
        catch (Exception ex)
        {
            throw new ModelLoadException($"Cannot load TMDL model from '{folder}': {ex.Message}", ex);
        }
    }

    /// <summary>One undo step: the model before and after a committed transaction, and its changes.</summary>
    private sealed class UndoStep(
        TomCheckpoint before, object beforeContent, object afterContent, IReadOnlyList<ModelChange> changes,
        string transaction, LiveLeaseOptions options)
    {
        public TomCheckpoint Before { get; } = before;

        /// <summary>Taken by the first undo, while the model is still in this state.</summary>
        public TomCheckpoint? After { get; set; }

        public object BeforeContent { get; } = beforeContent;

        public object AfterContent { get; } = afterContent;

        public IReadOnlyList<ModelChange> Changes { get; } = changes;

        public LiveHistoryStep ToHistory(bool undone) => new(transaction, options.Label, options.Client, Changes, undone);
    }

    /// <summary>The leases of one asynchronous flow: the innermost open one, if any.</summary>
    internal sealed class LeaseFlow
    {
        public Lease? Current { get; set; }
    }

    internal sealed class Lease : ILiveSessionLease
    {
        private readonly TomLiveModelSession _session;
        private int _ended;

        public Lease(TomLiveModelSession session, LeaseFlow flow, Lease? parent, LiveLeaseOptions options, string transaction, long baseVersion,
            bool isExplicit = false)
        {
            _session = session;
            Flow = flow;
            Parent = parent;
            Options = options;
            Transaction = transaction;
            BaseVersion = baseVersion;
            IsExplicit = isExplicit;
            Turns = isExplicit ? new LeaseGate() : null;
            Session = session.Source is TomServerModelSource server
                ? new TomServerLeaseView(session, this, server)
                : new TomLeaseView(session, this);
        }

        public LeaseFlow Flow { get; }

        public Lease? Parent { get; }

        public Lease Root => Parent?.Root ?? this;

        public LiveLeaseOptions Options { get; }

        /// <summary>The journal entry count when an in-place save last ran in this transaction.</summary>
        public int? EntriesAtSave { get; set; }

        public bool IsActive => Volatile.Read(ref _ended) == 0;

        public IModelSession Session { get; }

        public string Transaction { get; }

        public long BaseVersion { get; }

        public bool IsJoined => Parent is not null;

        /// <summary>An explicit transaction (<see cref="BeginTransactionAsync"/>).</summary>
        public bool IsExplicit { get; }

        /// <summary>For an explicit transaction: one turn at a time for the leases that join it,
        /// and for ending it.</summary>
        public LeaseGate? Turns { get; }

        /// <summary>True for a lease that joined an explicit transaction from its own flow; it
        /// holds the transaction's turn until it ends.</summary>
        public bool HoldsTurn => Parent is { IsExplicit: true } && !ReferenceEquals(Parent.Flow, Flow);

        public Task<ModelChangeBatch?> CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            return _session.CommitAsync(this, cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            return _session.RollbackAsync(this, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (!IsActive)
                return;

            // An explicit transaction waits for the lease running inside it, which may end it.
            if (IsExplicit && !await TakeTurnAsync(CancellationToken.None).ConfigureAwait(false))
                return;

            _session.Rollback(this);
        }

        /// <summary>For an explicit transaction: waits for its turn. False, with the turn passed
        /// on, when the transaction ended meanwhile.</summary>
        public async Task<bool> TakeTurnAsync(CancellationToken cancellationToken)
        {
            await Turns!.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (IsActive)
                return true;

            Turns.Release();
            return false;
        }

        public void ThrowIfEnded() => ObjectDisposedException.ThrowIf(!IsActive, this);

        public void End()
        {
            Interlocked.Exchange(ref _ended, 1);
            // A lease that joined an explicit transaction leaves its own flow with no lease.
            if (ReferenceEquals(Flow.Current, this))
                Flow.Current = Parent is not null && ReferenceEquals(Parent.Flow, Flow) ? Parent : null;
        }
    }
}
