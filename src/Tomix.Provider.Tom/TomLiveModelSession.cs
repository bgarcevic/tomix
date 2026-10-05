using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// A TOM model held open for many requests (ADR 0001), for TMDL, <c>.bim</c> and XMLA sources.
/// All access to the <see cref="Database"/> goes through a FIFO <see cref="LeaseGate"/>; a lease
/// is a <see cref="TomChangeJournal"/> transaction and the only way to reach the model
/// (ADR 0002 §1). Change events come from the journal; rollback restores its checkpoints
/// (ADR 0003).
/// </summary>
/// <remarks>
/// Undo, redo and explicit transactions arrive with #346 and throw
/// <see cref="NotSupportedException"/> until then. Detecting a source changed on disk
/// (<see cref="SessionState.Stale"/>) is not implemented yet.
/// </remarks>
public sealed class TomLiveModelSession : ILiveModelSession
{
    private readonly TomModelSource _source;
    private readonly LeaseGate _gate = new();
    private readonly AsyncLocal<LeaseFlow?> _flow = new();
    private readonly object _sync = new();
    private long _version;
    private long? _savedVersion = 0;
    private bool _saving;
    private bool _closed;
    private int _closing;
    private int _transactions;
    private SessionState _state = SessionState.Clean;
    private LiveModelSnapshot? _snapshot;

    internal TomLiveModelSession(TomModelSource source)
    {
        _source = source;
        Journal = new TomChangeJournal(source.Load(), source.Restore);
    }

    /// <summary>Opens a live session over a TMDL folder (the <c>definition</c> folder itself).</summary>
    /// <exception cref="ModelLoadException">The folder cannot be read as TMDL.</exception>
    public static ILiveModelSession OpenTmdlFolder(ModelReference reference, string folder, IAccessTokenProvider? tokenProvider = null)
        => new TomLiveModelSession(TomModelSource.File(reference, folder, "tmdl", LoadTmdlFolder, tokenProvider));

    internal TomChangeJournal Journal { get; }

    internal TomModelSource Source => _source;

    public ModelReference Reference => _source.Reference;

    public string SourcePath => _source.SourcePath;

    public SessionState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsDirty
    {
        get { lock (_sync) return _savedVersion != _version; }
    }

    public long Version
    {
        get { lock (_sync) return _version; }
    }

    public bool CanUndo => false;

    public bool CanRedo => false;

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
        return AcquireAsync(flow, options, cancellationToken);
    }

    public Task<ILiveSessionLease> BeginTransactionAsync(LiveLeaseOptions options, CancellationToken cancellationToken)
        => throw new NotSupportedException("Explicit transactions are not available yet (#346).");

    public Task<ModelChangeBatch?> UndoAsync(string? client, CancellationToken cancellationToken)
        => throw new NotSupportedException("Undo is not available yet (#346).");

    public Task<ModelChangeBatch?> RedoAsync(string? client, CancellationToken cancellationToken)
        => throw new NotSupportedException("Redo is not available yet (#346).");

    public async Task<LiveModelSnapshot> GetLiveSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _snapshot);
        if (cached is not null && cached.Version == Version)
            return cached;

        return await ReadAsync(() =>
        {
            if (_snapshot is null || _snapshot.Version != _version)
                Volatile.Write(ref _snapshot, new LiveModelSnapshot(_version, Snapshot()));
            return _snapshot!;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => (await GetLiveSnapshotAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
        => ReadAsync(() => _source.Summarize(Journal.Database), cancellationToken);

    /// <summary>Closes the session once the current lease ends. Leases still waiting fail with
    /// <see cref="ObjectDisposedException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_flow.Value?.Current is not null)
            throw new InvalidOperationException("A live session cannot be closed from inside one of its leases.");
        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return;

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync)
                _closed = true;
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

    private async Task<ILiveSessionLease> AcquireAsync(LeaseFlow flow, LiveLeaseOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Lease lease;
        try
        {
            ThrowIfClosed();
            Journal.Begin();
            lease = new Lease(this, flow, parent: null, options, $"t{++_transactions}", Version);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        flow.Current = lease;
        return lease;
    }

    private Lease Join(Lease outer, LiveLeaseOptions options)
    {
        Journal.Begin();
        var lease = new Lease(this, outer.Flow, outer, options, outer.Transaction, Version);
        outer.Flow.Current = lease;
        return lease;
    }

    private ModelChangeBatch? Commit(Lease lease)
    {
        if (!ReferenceEquals(lease.Flow.Current, lease))
            throw new InvalidOperationException("A lease opened inside this one is still open; end it first.");

        if (lease.IsJoined)
        {
            Journal.Commit();
            lease.End();
            return null;
        }

        try
        {
            var entries = Journal.Entries.Count;
            var changes = Journal.Commit();
            lease.End();

            ModelChangeBatch? batch = null;
            lock (_sync)
            {
                if (changes.Count > 0)
                {
                    _version++;
                    batch = new ModelChangeBatch(_version, lease.Transaction,
                        new ChangeOrigin(lease.Options.Client, ChangeOriginKind.Apply), changes);
                }

                if (lease.EntriesAtSave is { } atSave)
                    _savedVersion = atSave == entries ? _version : null;
            }

            if (batch is not null)
                Changed?.Invoke(this, batch);
            UpdateState();
            return batch;
        }
        finally
        {
            _gate.Release();
        }
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
                lease.End();
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
                    _savedVersion = atSave == 0 ? _version : null;
            }

            UpdateState();
        }
        finally
        {
            lease.End();
            _gate.Release();
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
                : _savedVersion != _version ? SessionState.Dirty
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

    /// <summary>The leases of one asynchronous flow: the innermost open one, if any.</summary>
    internal sealed class LeaseFlow
    {
        public Lease? Current { get; set; }
    }

    internal sealed class Lease : ILiveSessionLease
    {
        private readonly TomLiveModelSession _session;
        private int _ended;

        public Lease(TomLiveModelSession session, LeaseFlow flow, Lease? parent, LiveLeaseOptions options, string transaction, long baseVersion)
        {
            _session = session;
            Flow = flow;
            Parent = parent;
            Options = options;
            Transaction = transaction;
            BaseVersion = baseVersion;
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

        public Task<ModelChangeBatch?> CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            return Task.FromResult(_session.Commit(this));
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            _session.Rollback(this);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (IsActive)
                _session.Rollback(this);
            return ValueTask.CompletedTask;
        }

        public void ThrowIfEnded() => ObjectDisposedException.ThrowIf(!IsActive, this);

        public void End()
        {
            Interlocked.Exchange(ref _ended, 1);
            if (ReferenceEquals(Flow.Current, this))
                Flow.Current = Parent;
        }
    }
}
