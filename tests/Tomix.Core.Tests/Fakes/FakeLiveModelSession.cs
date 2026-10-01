using Tomix.Core.Models;

namespace Tomix.Core.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ILiveModelSession"/> over a <see cref="ModelSnapshot"/> tree, for
/// exercising the live-session contract without a provider. It supports one mutation, renaming
/// through <c>SetProperty("name", ...)</c>, which is enough to show that IDs survive a path change.
/// Leases are exclusive (one at a time) but never join; undo and explicit transactions are not
/// implemented.
/// </summary>
internal sealed class FakeLiveModelSession : ILiveModelSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelSnapshot _committed;
    private long _version;
    private long _transactions;

    /// <summary>Assigns IDs to every object in <paramref name="snapshot"/> in tree order, starting at <c>o1</c>.</summary>
    public FakeLiveModelSession(ModelSnapshot snapshot)
    {
        long next = 0;
        _committed = snapshot with { Objects = Stamp(snapshot.Objects) };

        IReadOnlyList<ModelObject> Stamp(IReadOnlyList<ModelObject> nodes)
            => nodes.Select(n =>
            {
                var id = new ObjectId(++next);
                return n with { Id = id, Children = Stamp(n.Children) };
            }).ToList();
    }

    public ModelReference Reference { get; } = new("fake");

    public string SourcePath => Reference.Value;

    public SessionState State => SessionState.Clean;

    public bool IsDirty => false;

    public long Version => Interlocked.Read(ref _version);

    public bool CanUndo => false;

    public bool CanRedo => false;

    public event EventHandler<ModelChangeBatch>? Changed;

    public event EventHandler<SessionStateChange>? StateChanged { add { } remove { } }

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(Volatile.Read(ref _committed));

    public Task<LiveModelSnapshot> GetLiveSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LiveModelSnapshot(Version, Volatile.Read(ref _committed)));

    public async Task<ILiveSessionLease> LeaseAsync(LiveLeaseOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this, options, $"t{++_transactions}");
    }

    public Task<ILiveSessionLease> BeginTransactionAsync(LiveLeaseOptions options, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ModelChangeBatch?> UndoAsync(string? client, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ModelChangeBatch?> RedoAsync(string? client, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Lease : ILiveSessionLease
    {
        private readonly FakeLiveModelSession _owner;
        private readonly LiveLeaseOptions _options;
        private readonly LeasedSession _session;
        private readonly List<ModelChange> _changes = [];
        private ModelSnapshot _working;
        private bool _ended;

        public Lease(FakeLiveModelSession owner, LiveLeaseOptions options, string transaction)
        {
            _owner = owner;
            _options = options;
            _working = owner._committed;
            Transaction = transaction;
            BaseVersion = owner.Version;
            _session = new LeasedSession(this);
        }

        public IModelSession Session => _session;

        public string Transaction { get; }

        public long BaseVersion { get; }

        public bool IsJoined => false;

        public Task<ModelChangeBatch?> CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            ModelChangeBatch? batch = null;
            if (_changes.Count > 0)
            {
                Volatile.Write(ref _owner._committed, _working);
                var version = Interlocked.Increment(ref _owner._version);
                batch = new ModelChangeBatch(version, Transaction,
                    new ChangeOrigin(_options.Client, ChangeOriginKind.Apply), _changes.ToList());
            }

            End();
            if (batch is not null)
                _owner.Changed?.Invoke(_owner, batch);

            return Task.FromResult(batch);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            ThrowIfEnded();
            End();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_ended)
                End();

            return ValueTask.CompletedTask;
        }

        private void End()
        {
            _ended = true;
            _owner._gate.Release();
        }

        private void ThrowIfEnded()
            => ObjectDisposedException.ThrowIf(_ended, this);

        private ModelObjectMutationResult Rename(ModelObjectSetRequest request)
        {
            ThrowIfEnded();
            var assignment = Assert.Single(request.Properties);
            if (!string.Equals(assignment.Property, "name", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The fake session only renames.");

            var index = ModelObjectIndex.Build(_working);
            if (!index.TryGetId(request.Path, out var id))
                throw new InvalidOperationException($"Object not found: {request.Path}");

            index.TryGetObject(id, out var target);
            var newPath = ParentPath(target.Path) is { } parent ? $"{parent}/{assignment.Value}" : assignment.Value;
            _working = _working with { Objects = Rewrite(_working.Objects) };
            _changes.Add(new ModelChange(id, target.Kind, ModelChangeKind.Renamed, newPath, OldPath: target.Path));
            return new ModelObjectMutationResult(newPath, Changed: true, "name", assignment.Value);

            IReadOnlyList<ModelObject> Rewrite(IReadOnlyList<ModelObject> nodes)
                => nodes.Select(n => n.Id == id
                        ? Repath(n with { Name = assignment.Value }, newPath)
                        : n with { Children = Rewrite(n.Children) })
                    .ToList();
        }

        private static ModelObject Repath(ModelObject node, string path)
            => node with
            {
                Path = path,
                Children = node.Children.Select(c => Repath(c, $"{path}/{c.Name}")).ToList()
            };

        private static string? ParentPath(string path)
            => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : null;

        private sealed class LeasedSession(Lease lease) : IModelSession, IModelMutationSession
        {
            public string SourcePath
            {
                get
                {
                    lease.ThrowIfEnded();
                    return lease._owner.SourcePath;
                }
            }

            public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            {
                lease.ThrowIfEnded();
                return Task.FromResult(lease._working);
            }

            public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => lease.Rename(request);

            public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw new NotSupportedException();

            public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();

            public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();

            public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
