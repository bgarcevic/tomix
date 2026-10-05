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
/// before it, kept with the changes it made. Detecting a source changed on disk
/// (<see cref="SessionState.Stale"/>) is not implemented yet.
/// </remarks>
public sealed class TomLiveModelSession : ILiveModelSession
{
    /// <summary>Undo steps kept by default. Each holds a full copy of the model (ADR 0003 §3),
    /// so memory grows with model size times this number.</summary>
    public const int DefaultUndoLimit = 50;

    /// <summary>How long an explicit transaction may sit with no lease in it before it is
    /// rolled back, so a client that went away cannot hold the model forever.</summary>
    public static readonly TimeSpan DefaultTransactionIdleTimeout = TimeSpan.FromMinutes(15);

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

    internal TomLiveModelSession(TomModelSource source)
    {
        _source = source;
        _savedContent = _content;
        Journal = new TomChangeJournal(source.Load(), source.Restore);
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
            var result = await _source.SaveAsync(Journal.Database, outputPath, serialization, overwrite, cancellationToken).ConfigureAwait(false);
            if (_source.IsInPlace(outputPath))
                lease.Root.EntriesAtSave = Journal.Entries.Count;
            return result;
        }
        finally
        {
            lock (_sync)
                _saving = false;
            UpdateState();
        }
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
