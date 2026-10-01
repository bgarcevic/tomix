using Tomix.Core.Models;
using Tomix.Core.Tests.Fakes;

namespace Tomix.Core.Tests;

/// <summary>
/// The live-session contract as a client sees it, against <see cref="FakeLiveModelSession"/>:
/// ID/path lookups, IDs that survive a rename, versions and change batches published on commit,
/// rollback on an uncommitted lease, and a lease's session that dies with the lease.
/// </summary>
public sealed class LiveModelSessionContractTests
{
    private static readonly ModelSnapshot Model = new("M", 1600,
    [
        Node("Sales", ModelObjectKind.Table, "Sales",
            Node("Amount", ModelObjectKind.Column, "Sales/Amount"),
            Node("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales")),
        Node("Date", ModelObjectKind.Table, "Date",
            Node("Year", ModelObjectKind.Column, "Date/Year"))
    ]);

    [Fact]
    public async Task Index_MapsIdsAndPathsBothWays()
    {
        await using var session = new FakeLiveModelSession(Model);
        var live = await session.GetLiveSnapshotAsync(CancellationToken.None);

        Assert.Equal(5, live.Index.Count);
        Assert.True(live.Index.TryGetId("sales/total sales", out var id));
        Assert.Equal("o3", id.ToString());
        Assert.True(live.Index.TryGetPath(id, out var path));
        Assert.Equal("Sales/Total Sales", path);
        Assert.True(live.Index.TryGetObject(id, out var measure));
        Assert.Equal(ModelObjectKind.Measure, measure.Kind);
    }

    [Fact]
    public async Task Index_MissesUnknownIdsAndPaths()
    {
        await using var session = new FakeLiveModelSession(Model);
        var live = await session.GetLiveSnapshotAsync(CancellationToken.None);

        Assert.False(live.Index.TryGetId("Sales/Nope", out _));
        Assert.False(live.Index.TryGetPath(new ObjectId(99), out _));
    }

    [Fact]
    public async Task Rename_KeepsIdsAndMovesPathsOfObjectAndDescendants()
    {
        await using var session = new FakeLiveModelSession(Model);
        var before = await session.GetLiveSnapshotAsync(CancellationToken.None);
        before.Index.TryGetId("Sales", out var tableId);
        before.Index.TryGetId("Sales/Amount", out var columnId);

        await RenameAsync(session, "Sales", "Revenue");

        var after = await session.GetLiveSnapshotAsync(CancellationToken.None);
        Assert.True(after.Index.TryGetPath(tableId, out var tablePath));
        Assert.Equal("Revenue", tablePath);
        Assert.True(after.Index.TryGetPath(columnId, out var columnPath));
        Assert.Equal("Revenue/Amount", columnPath);
        Assert.False(after.Index.TryGetId("Sales", out _));

        // The earlier snapshot is immutable and still answers for its own version.
        Assert.True(before.Index.TryGetPath(tableId, out var oldPath));
        Assert.Equal("Sales", oldPath);
    }

    [Fact]
    public async Task Commit_PublishesOneVersionAndOneBatch()
    {
        await using var session = new FakeLiveModelSession(Model);
        var batches = new List<ModelChangeBatch>();
        session.Changed += (_, batch) => batches.Add(batch);

        await RenameAsync(session, "Sales/Total Sales", "Revenue", client: "mcp-1");

        Assert.Equal(1, session.Version);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Version);
        Assert.Equal(new ChangeOrigin("mcp-1", ChangeOriginKind.Apply), batch.Origin);
        var change = Assert.Single(batch.Changes);
        Assert.Equal(ModelChangeKind.Renamed, change.Change);
        Assert.Equal("Sales/Revenue", change.Path);
        Assert.Equal("Sales/Total Sales", change.OldPath);
        Assert.Equal("o3", change.Id.ToString());
    }

    [Fact]
    public async Task Commit_WithoutChanges_PublishesNothing()
    {
        await using var session = new FakeLiveModelSession(Model);
        await using var lease = await session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None);

        Assert.Null(await lease.CommitAsync(CancellationToken.None));
        Assert.Equal(0, session.Version);
    }

    [Fact]
    public async Task DisposeWithoutCommit_RollsBack()
    {
        await using var session = new FakeLiveModelSession(Model);
        var batches = 0;
        session.Changed += (_, _) => batches++;

        await using (var lease = await session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None))
        {
            Mutator(lease).SetProperty(Rename("Sales", "Revenue"));
            var working = await lease.Session.GetSnapshotAsync(CancellationToken.None);
            Assert.Contains(working.Objects, o => o.Name == "Revenue");
        }

        Assert.Equal(0, session.Version);
        Assert.Equal(0, batches);
        var live = await session.GetLiveSnapshotAsync(CancellationToken.None);
        Assert.True(live.Index.TryGetId("Sales", out _));
    }

    [Fact]
    public async Task LeaseSession_ThrowsAfterTheLeaseEnds()
    {
        await using var session = new FakeLiveModelSession(Model);
        var lease = await session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None);
        var leased = lease.Session;
        await lease.CommitAsync(CancellationToken.None);

        Assert.Throws<ObjectDisposedException>(() => Mutator(lease).SetProperty(Rename("Sales", "Revenue")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => leased.GetSnapshotAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Leases_AreExclusive()
    {
        await using var session = new FakeLiveModelSession(Model);
        var first = await session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None);

        var second = session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var granted = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, granted.BaseVersion);
    }

    [Fact]
    public async Task Lease_CancelledWhileWaiting_IsNeverGranted()
    {
        await using var session = new FakeLiveModelSession(Model);
        await using var holder = await session.LeaseAsync(new LiveLeaseOptions(), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = session.LeaseAsync(new LiveLeaseOptions(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static async Task RenameAsync(ILiveModelSession session, string path, string name, string? client = null)
    {
        await using var lease = await session.LeaseAsync(new LiveLeaseOptions(client), CancellationToken.None);
        Mutator(lease).SetProperty(Rename(path, name));
        await lease.CommitAsync(CancellationToken.None);
    }

    private static IModelMutationSession Mutator(ILiveSessionLease lease)
        => Assert.IsAssignableFrom<IModelMutationSession>(lease.Session);

    private static ModelObjectSetRequest Rename(string path, string name)
        => new(path, [new ModelPropertyAssignment("name", name)], Type: null);

    private static ModelObject Node(string name, ModelObjectKind kind, string path, params ModelObject[] children)
        => new(name, kind, path, Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: children);
}
