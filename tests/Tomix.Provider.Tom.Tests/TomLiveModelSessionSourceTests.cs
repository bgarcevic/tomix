using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Tests.Support;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// A live session on files notices when they change outside it (#351): it turns stale, refuses
/// to save over the change until the caller keeps its own version, and can reload the files.
/// </summary>
public sealed class TomLiveModelSessionSourceTests
{
    private static readonly LiveLeaseOptions Shell = new("shell");

    [Fact]
    public async Task EditingAFileOutsideTheSession_MakesItStale_AndUndoingTheEditClearsIt()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        var sales = dir.Combine("tables", "Sales.tmdl");
        var original = File.ReadAllText(sales);

        Assert.False(await session.CheckSourceAsync(CancellationToken.None));
        File.WriteAllText(sales, original.Replace("SUM ( Sales[Amount] )", "SUM ( Sales[Amount] ) * 3"));

        Assert.True(await session.CheckSourceAsync(CancellationToken.None));
        Assert.Equal(SessionState.Stale, session.State);
        Assert.False(session.IsDirty);

        File.WriteAllText(sales, original);
        Assert.False(await session.CheckSourceAsync(CancellationToken.None));
        Assert.Equal(SessionState.Clean, session.State);
    }

    [Fact]
    public async Task TheSessionsOwnSave_DoesNotMakeItStale()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);

        await EditAndSaveAsync(session, "1");

        Assert.False(await session.CheckSourceAsync(CancellationToken.None));
        Assert.Equal(SessionState.Clean, session.State);
    }

    [Fact]
    public async Task SavingOverAChangeMadeOutside_Fails_AndLeavesTheFilesAlone()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        var sales = dir.Combine("tables", "Sales.tmdl");
        var theirs = File.ReadAllText(sales).Replace("COUNTROWS(Sales)", "COUNTROWS(Sales) + 1");
        File.WriteAllText(sales, theirs);

        var ex = await Assert.ThrowsAsync<ModelSourceChangedException>(() => EditAndSaveAsync(session, "1"));

        Assert.Equal(dir.Path, ex.SourcePath);
        Assert.Equal(theirs, File.ReadAllText(sales));
        Assert.Equal(SessionState.Stale, session.State);
    }

    [Fact]
    public async Task KeepingTheSessionsChanges_LetsTheNextSaveOverwriteTheFiles()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        var sales = dir.Combine("tables", "Sales.tmdl");
        File.WriteAllText(sales, File.ReadAllText(sales).Replace("COUNTROWS(Sales)", "COUNTROWS(Sales) + 1"));
        Assert.True(await session.CheckSourceAsync(CancellationToken.None));

        await using (var lease = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            var external = Assert.IsAssignableFrom<IExternalChangeSession>(lease.Session);
            Assert.True(external.SourceChanged);
            ((IModelMutationSession)lease.Session).SetProperty(Set("Sales/Total Sales", "expression", "42"));
            external.KeepChanges();
            await ((IModelMutationSession)lease.Session).SaveAsync(null, "", overwrite: true, CancellationToken.None);
            await lease.CommitAsync(CancellationToken.None);
        }

        var saved = File.ReadAllText(sales);
        Assert.Contains("= 42", saved);
        Assert.DoesNotContain("+ 1", saved);
        Assert.Equal(SessionState.Clean, session.State);
    }

    [Fact]
    public async Task Reloading_TakesTheFilesVersion_KeepsIds_AndClearsUndo()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        var before = Flatten((await session.GetLiveSnapshotAsync(CancellationToken.None)).Snapshot);
        await using (var lease = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            ((IModelMutationSession)lease.Session).SetProperty(Set("Customers", "description", "unsaved"));
            await lease.CommitAsync(CancellationToken.None);
        }

        var sales = dir.Combine("tables", "Sales.tmdl");
        File.WriteAllText(sales, File.ReadAllText(sales)
            .Replace("SUM ( Sales[Amount] )", "SUM ( Sales[Amount] ) * 3")
            .Replace("\tmeasure 'Order Count' = COUNTROWS(Sales)", "\tmeasure 'Line Count' = COUNTROWS(Sales)"));
        var batches = new List<ModelChangeBatch>();
        session.Changed += (_, batch) => batches.Add(batch);

        var reloaded = await session.ReloadAsync("ui-1", CancellationToken.None);

        Assert.Same(reloaded, Assert.Single(batches));
        Assert.Equal(new ChangeOrigin("ui-1", ChangeOriginKind.Reload), reloaded.Origin);
        var changes = reloaded.Changes.ToDictionary(change => change.Path);
        Assert.Equal(ModelChangeKind.Modified, changes["Sales/Total Sales"].Change);
        Assert.Equal(before["Sales/Total Sales"], changes["Sales/Total Sales"].Id);
        Assert.Equal(ModelChangeKind.Modified, changes["Customers"].Change);
        Assert.Equal(ModelChangeKind.Removed, changes["Sales/Order Count"].Change);
        Assert.Equal(ModelChangeKind.Added, changes["Sales/Line Count"].Change);

        var after = (await session.GetLiveSnapshotAsync(CancellationToken.None)).Snapshot;
        Assert.Equal(before["Sales"], Flatten(after)["Sales"]);
        Assert.Contains("* 3", Find(after, "Sales/Total Sales").Expression);
        Assert.Null(Find(after, "Customers").Description);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
        Assert.Equal(SessionState.Clean, session.State);
    }

    [Fact]
    public async Task ReloadingUnchangedFiles_PublishesAnEmptyBatch()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);

        var reloaded = await session.ReloadAsync(null, CancellationToken.None);

        Assert.Empty(reloaded.Changes);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task ReloadingFilesThatNoLongerLoad_FailsAndKeepsTheModel()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        File.WriteAllText(dir.Combine("tables", "Sales.tmdl"), "table Sales\n\tmeasure = = =\n");

        await Assert.ThrowsAsync<ModelLoadException>(() => session.ReloadAsync(null, CancellationToken.None));

        Assert.Equal(0, session.Version);
        Assert.Equal("SUM ( Sales[Amount] )", Find((await session.GetLiveSnapshotAsync(CancellationToken.None)).Snapshot, "Sales/Total Sales").Expression);
    }

    [Fact]
    public async Task TheSessionWatchesItsFiles_AndTurnsStaleOnItsOwn()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        await using var session = await Open(dir.Path);
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, change) =>
        {
            if (change.Current == SessionState.Stale)
                stale.TrySetResult();
        };

        var model = dir.Combine("model.tmdl");
        File.AppendAllText(model, "\n");

        await stale.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ABimFileChangedOutside_MakesTheSessionStale()
    {
        using var dir = new TempDir();
        var bim = dir.Combine("model.bim");
        File.Copy(Path.Combine(RepoPaths.Samples, "basic-tmdl.bim"), bim);
        await using var session = await new TomFileModelProvider().OpenLiveAsync(new ModelReference(bim), CancellationToken.None);

        File.WriteAllText(bim, File.ReadAllText(bim).Replace("SUM ( Sales[Amount] )", "SUM ( Sales[Amount] ) * 3"));

        Assert.True(await session.CheckSourceAsync(CancellationToken.None));
        Assert.True(session.CanReload);
        var reloaded = await session.ReloadAsync(null, CancellationToken.None);
        Assert.Contains(reloaded.Changes, change => change.Path == "Sales/Total Sales" && change.Change == ModelChangeKind.Modified);
    }

    private static Task<ILiveModelSession> Open(string folder)
        => new TmdlModelProvider().OpenLiveAsync(new ModelReference(folder), CancellationToken.None);

    private static async Task EditAndSaveAsync(ILiveModelSession session, string expression)
    {
        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        var mutator = (IModelMutationSession)lease.Session;
        mutator.SetProperty(Set("Sales/Total Sales", "expression", expression));
        await mutator.SaveAsync(null, "", overwrite: true, CancellationToken.None);
        await lease.CommitAsync(CancellationToken.None);
    }

    private static Dictionary<string, ObjectId?> Flatten(ModelSnapshot snapshot)
    {
        var all = new Dictionary<string, ObjectId?>();
        void Walk(IEnumerable<ModelObject> objects)
        {
            foreach (var item in objects)
            {
                all.TryAdd(item.Path, item.Id);
                Walk(item.Children);
            }
        }

        Walk(snapshot.Objects);
        return all;
    }

    private static ModelObject Find(ModelSnapshot snapshot, string path)
    {
        ModelObject? Search(IEnumerable<ModelObject> objects)
            => objects.Select(item => item.Path == path ? item : Search(item.Children)).FirstOrDefault(found => found is not null);
        return Search(snapshot.Objects) ?? throw new KeyNotFoundException(path);
    }
}
