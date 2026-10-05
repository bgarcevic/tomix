using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Tests.Support;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <see cref="TomLiveModelSession"/>: saves match the one-shot commands byte for byte, a lease is
/// a transaction (commit publishes one batch, disposal rolls back), leases are granted in order and
/// join within one flow, and the session tracks its save point.
/// </summary>
public sealed class TomLiveModelSessionTests
{
    private static readonly string BasicTmdl = Path.Combine(RepoPaths.Samples, "basic-tmdl");
    private static readonly LiveLeaseOptions Shell = new("shell");

    /// <summary>One request's worth of edits, covering every mutation kind and a cascade.</summary>
    private static void Edit(IModelSession session)
    {
        var mutator = (IModelMutationSession)session;
        mutator.SetProperty(Set("Sales/Total Sales", "expression", "SUM ( Sales[Amount] ) * 2"));
        mutator.AddObject(Add("Sales/Margin", "Measure", "[Total Sales] - 1"));
        mutator.SetProperty(Set("Customers", "name", "Clients"));
        mutator.RemoveObject(Remove("Products/Price"));
        ((IObjectMoveSession)session).MoveObject(Move("Sales/Avg Sale", "Clients"));
        mutator.ReplaceText(Replace("DISTINCTCOUNT", "DISTINCTCOUNTNOBLANK", "expressions"));
    }

    [Fact]
    public async Task SavingATmdlFolder_WritesTheSameFilesAsTheOneShotCommands()
    {
        using var oneShotDir = CopyOfBasicTmdl();
        using var liveDir = CopyOfBasicTmdl();

        await using (var oneShot = new TmdlModelSession(oneShotDir.Path))
        {
            Edit(oneShot);
            await oneShot.SaveAsync(null, "", overwrite: true, CancellationToken.None);
        }

        await using (var live = await new TmdlModelProvider().OpenLiveAsync(new ModelReference(liveDir.Path), CancellationToken.None))
        {
            await using var lease = await live.LeaseAsync(Shell, CancellationToken.None);
            Edit(lease.Session);
            await ((IModelMutationSession)lease.Session).SaveAsync(null, "", overwrite: true, CancellationToken.None);
            await lease.CommitAsync(CancellationToken.None);
        }

        AssertSameFiles(oneShotDir.Path, liveDir.Path);
    }

    [Fact]
    public async Task SavingABimFile_WritesTheSameFileAsTheOneShotCommands()
    {
        using var dir = new TempDir();
        var oneShotPath = dir.Combine("one-shot.bim");
        var livePath = dir.Combine("live.bim");
        File.Copy(Path.Combine(RepoPaths.Samples, "basic-tmdl.bim"), oneShotPath);
        File.Copy(Path.Combine(RepoPaths.Samples, "basic-tmdl.bim"), livePath);
        var provider = new TomFileModelProvider();

        await using (var oneShot = await provider.OpenAsync(new ModelReference(oneShotPath), CancellationToken.None))
        {
            Edit(oneShot);
            await ((IModelMutationSession)oneShot).SaveAsync(null, "", overwrite: true, CancellationToken.None);
        }

        await using (var live = await provider.OpenLiveAsync(new ModelReference(livePath), CancellationToken.None))
        {
            await using var lease = await live.LeaseAsync(Shell, CancellationToken.None);
            Edit(lease.Session);
            await ((IModelMutationSession)lease.Session).SaveAsync(null, "", overwrite: true, CancellationToken.None);
            await lease.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(File.ReadAllBytes(oneShotPath), File.ReadAllBytes(livePath));
    }

    [Fact]
    public async Task Commit_PublishesOneBatchWithTheJournalsChanges()
    {
        await using var session = OpenBasic();
        var before = (await session.GetLiveSnapshotAsync(CancellationToken.None)).Index;
        var batches = new List<ModelChangeBatch>();
        session.Changed += (_, batch) => batches.Add(batch);

        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        var mutator = (IModelMutationSession)lease.Session;
        mutator.SetProperty(Set("Sales/Total Sales", "expression", "1"));
        mutator.RemoveObject(Remove("Customers"));
        var batch = await lease.CommitAsync(CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal([batch], batches);
        Assert.Equal((1L, "t1", new ChangeOrigin("shell", ChangeOriginKind.Apply)), (batch.Version, batch.Transaction, batch.Origin));
        Assert.Equal(1, session.Version);
        Assert.True(before.TryGetId("Sales/Total Sales", out var measure));
        Assert.True(before.TryGetId("Customers", out var customers));
        Assert.True(before.TryGetId("Relationships/rel-customers", out var relationship));
        var modified = Assert.Single(batch.Changes, c => c.Id == measure);
        Assert.Equal((ModelChangeKind.Modified, "Sales/Total Sales"), (modified.Change, modified.Path));
        Assert.Equal(["Expression"], modified.Properties);
        Assert.Contains(batch.Changes, c => c.Id == customers && c.Change == ModelChangeKind.Removed);
        Assert.Contains(batch.Changes, c => c.Id == relationship && c.Change == ModelChangeKind.Removed);
    }

    [Fact]
    public async Task CommittingNothing_PublishesNothingAndKeepsTheVersion()
    {
        await using var session = OpenBasic();
        var raised = false;
        session.Changed += (_, _) => raised = true;

        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        await lease.Session.GetSnapshotAsync(CancellationToken.None);

        Assert.Null(await lease.CommitAsync(CancellationToken.None));
        Assert.False(raised);
        Assert.Equal(0, session.Version);
    }

    [Fact]
    public async Task ARequestThatThrows_LeavesTheModelAsItFoundIt()
    {
        await using var session = OpenBasic();
        var before = await session.GetSnapshotAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
            Edit(lease.Session);
            throw new InvalidOperationException("the request failed partway");
        });

        Assert.Equal(0, session.Version);
        Assert.False(session.IsDirty);
        await using var next = await session.LeaseAsync(Shell, CancellationToken.None);
        Assert.Equal(Json(before), Json(await next.Session.GetSnapshotAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task TheLeaseView_StopsWorkingWhenTheLeaseEnds()
    {
        await using var session = OpenBasic();
        var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        var view = (IModelMutationSession)lease.Session;
        await lease.CommitAsync(CancellationToken.None);

        Assert.Throws<ObjectDisposedException>(() => view.SetProperty(Set("Sales/Total Sales", "expression", "1")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.Session.GetSnapshotAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.CommitAsync(CancellationToken.None));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Leases_AreGrantedInRequestOrder_AndACancelledWaiterNeverRuns()
    {
        await using var session = OpenBasic();
        var order = new List<string>();
        var first = await session.LeaseAsync(Shell, CancellationToken.None);

        var second = OtherClient(() => Take("second"));
        await WaitUntilQueued();
        using var cancel = new CancellationTokenSource();
        var cancelled = OtherClient(() => session.LeaseAsync(Shell, cancel.Token));
        await WaitUntilQueued();
        var third = OtherClient(() => Take("third"));
        await WaitUntilQueued();

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(second.IsCompleted);

        order.Add("first");
        await first.CommitAsync(CancellationToken.None);
        await Task.WhenAll(second, third);

        Assert.Equal(["first", "second", "third"], order);

        async Task Take(string name)
        {
            await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
            lock (order)
                order.Add(name);
        }
    }

    [Fact]
    public async Task ALeaseInsideALease_JoinsIt_AndRollsBackOnlyItsOwnChanges()
    {
        await using var session = OpenBasic();
        await using var outer = await session.LeaseAsync(Shell, CancellationToken.None);
        ((IModelMutationSession)outer.Session).SetProperty(Set("Sales/Total Sales", "expression", "1"));

        await using (var inner = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            Assert.True(inner.IsJoined);
            Assert.Equal(outer.Transaction, inner.Transaction);
            ((IModelMutationSession)inner.Session).SetProperty(Set("Sales/Order Count", "expression", "2"));
        }

        await using (var inner = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            ((IModelMutationSession)inner.Session).SetProperty(Set("Sales/Avg Sale", "expression", "3"));
            Assert.Null(await inner.CommitAsync(CancellationToken.None));
        }

        var batch = await outer.CommitAsync(CancellationToken.None);

        Assert.Equal(["Sales/Avg Sale", "Sales/Total Sales"], batch!.Changes.Select(c => c.Path).Order());
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public async Task TheLiveSnapshot_IsCachedPerVersion_AndKeepsIdsAcrossCommits()
    {
        await using var session = OpenBasic();
        var v0 = await session.GetLiveSnapshotAsync(CancellationToken.None);
        Assert.Same(v0, await session.GetLiveSnapshotAsync(CancellationToken.None));

        await using (var lease = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            ((IModelMutationSession)lease.Session).SetProperty(Set("Customers", "name", "Clients"));
            await lease.CommitAsync(CancellationToken.None);
        }

        var v1 = await session.GetLiveSnapshotAsync(CancellationToken.None);
        Assert.Equal((0L, 1L), (v0.Version, v1.Version));
        Assert.True(v0.Index.TryGetId("Customers/City", out var city));
        Assert.True(v1.Index.TryGetPath(city, out var path));
        Assert.Equal("Clients/City", path);
    }

    [Fact]
    public async Task ReadingTheLiveSnapshotInsideALease_Throws()
    {
        await using var session = OpenBasic();
        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        ((IModelMutationSession)lease.Session).SetProperty(Set("Customers", "name", "Clients"));
        await lease.CommitAsync(CancellationToken.None);

        // Committed, so this flow no longer holds a lease and the read succeeds.
        Assert.Equal(1, (await session.GetLiveSnapshotAsync(CancellationToken.None)).Version);

        await using var next = await session.LeaseAsync(Shell, CancellationToken.None);
        ((IModelMutationSession)next.Session).SetProperty(Set("Clients", "name", "Customers"));
        await next.CommitAsync(CancellationToken.None);
        await using var held = await session.LeaseAsync(Shell, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetLiveSnapshotAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheSessionIsDirtyAfterACommit_AndCleanAfterAnInPlaceSave()
    {
        using var dir = CopyOfBasicTmdl();
        await using var session = TomLiveModelSession.OpenTmdlFolder(new ModelReference(dir.Path), dir.Path);
        var states = new List<SessionState>();
        session.StateChanged += (_, change) => states.Add(change.Current);

        await Apply(session, s => s.SetProperty(Set("Sales/Total Sales", "expression", "1")));
        Assert.True(session.IsDirty);

        await Apply(session, s => s.SaveAsync(null, "", overwrite: true, CancellationToken.None).GetAwaiter().GetResult());

        Assert.False(session.IsDirty);
        Assert.Equal([SessionState.Dirty, SessionState.Saving, SessionState.Dirty, SessionState.Clean], states);
    }

    [Fact]
    public async Task SavingThenRollingBack_LeavesTheSessionDirty()
    {
        using var dir = CopyOfBasicTmdl();
        await using var session = TomLiveModelSession.OpenTmdlFolder(new ModelReference(dir.Path), dir.Path);

        await using (var lease = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            var mutator = (IModelMutationSession)lease.Session;
            mutator.SetProperty(Set("Sales/Total Sales", "expression", "1"));
            await mutator.SaveAsync(null, "", overwrite: true, CancellationToken.None);
        }

        // The disk has the edit; the model does not.
        Assert.Equal(0, session.Version);
        Assert.True(session.IsDirty);
        Assert.Equal(SessionState.Dirty, session.State);
    }

    [Fact]
    public async Task ClosingTheSession_FailsLaterLeases()
    {
        var session = OpenBasic();
        await session.DisposeAsync();

        Assert.Equal(SessionState.Closed, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.LeaseAsync(Shell, CancellationToken.None));
    }

    private static ILiveModelSession OpenBasic()
        => TomLiveModelSession.OpenTmdlFolder(new ModelReference(BasicTmdl), BasicTmdl);

    private static async Task Apply(ILiveModelSession session, Action<IModelMutationSession> edit)
    {
        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        edit((IModelMutationSession)lease.Session);
        await lease.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs <paramref name="request"/> as another client would: without this test's execution
    /// context, which carries the lease it holds. A plain <c>Task.Run</c> would inherit that
    /// lease and join it.
    /// </summary>
    private static Task<T> OtherClient<T>(Func<Task<T>> request)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(request);
    }

    private static Task OtherClient(Func<Task> request)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(request);
    }

    /// <summary>Gives a request started on another thread time to reach the gate's queue.</summary>
    private static Task WaitUntilQueued() => Task.Delay(100);

    internal static TempDir CopyOfBasicTmdl()
    {
        var dir = new TempDir();
        foreach (var file in Directory.GetFiles(BasicTmdl, "*", SearchOption.AllDirectories))
        {
            var target = dir.Combine(Path.GetRelativePath(BasicTmdl, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return dir;
    }

    internal static void AssertSameFiles(string expected, string actual)
    {
        static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(Files(expected), Files(actual));
        foreach (var file in Files(expected))
            Assert.True(File.ReadAllBytes(Path.Combine(expected, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(actual, file))), $"{file} differs");
    }

    private static string Json(ModelSnapshot snapshot) => System.Text.Json.JsonSerializer.Serialize(snapshot);
}
