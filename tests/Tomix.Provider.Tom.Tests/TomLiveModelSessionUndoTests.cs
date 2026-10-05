using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Tests.Support;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Undo, redo and explicit transactions on <see cref="TomLiveModelSession"/> (#346): undoing
/// every committed step brings the model back byte for byte and redoing them reproduces it, the
/// save point follows the undo position, and an explicit transaction groups a client's requests
/// into one step while other clients wait.
/// </summary>
public sealed class TomLiveModelSessionUndoTests
{
    private static readonly LiveLeaseOptions Shell = new("shell");
    private static readonly CancellationToken None = CancellationToken.None;

    public static TheoryData<string> Samples => new(
        new[] { Path.Combine(RepoPaths.Samples, "basic-tmdl") }
            .Concat(Directory.GetDirectories(RepoPaths.Samples, "*.SemanticModel").Select(d => Path.Combine(d, "definition")))
            .Select(d => Path.GetRelativePath(RepoPaths.Samples, d)));

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task UndoingRandomEdits_RestoresTheModel_AndRedoingThemReproducesIt(string sample)
    {
        var folder = Path.Combine(RepoPaths.Samples, sample);
        await using var session = (TomLiveModelSession)TomLiveModelSession.OpenTmdlFolder(new ModelReference(folder), folder);
        var original = Tmdl(session);
        var random = new Random(346);

        var committed = 0;
        for (var i = 0; i < 12; i++)
        {
            await using var lease = await session.LeaseAsync(Shell, None);
            var snapshot = await lease.Session.GetSnapshotAsync(None);
            try
            {
                RandomEdit(lease.Session, snapshot, random, i);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
                                           or ObjectNotFoundException or AmbiguousObjectException)
            {
                // Not valid on this model: the lease rolls it back.
                continue;
            }

            if (await lease.CommitAsync(None) is not null)
                committed++;
        }

        Assert.True(committed >= 6, $"Only {committed} random edits applied to {sample}.");
        var edited = Tmdl(session);
        Assert.NotEqual(original, edited);

        var undone = 0;
        while (await session.UndoAsync("shell", None) is not null)
            undone++;
        Assert.Equal(committed, undone);
        Assert.Equal(original, Tmdl(session));
        Assert.False(session.IsDirty);

        while (await session.RedoAsync("shell", None) is not null)
        {
        }

        Assert.Equal(edited, Tmdl(session));
        Assert.Equal(3 * committed, session.Version);
    }

    [Fact]
    public async Task UndoAfterASave_SavesBackTheOriginalFiles()
    {
        using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
        using var baseline = new TempDir();
        await using var session = TomLiveModelSession.OpenTmdlFolder(new ModelReference(dir.Path), dir.Path);
        await Save(session);
        SampleModel.CopyDirectory(dir.Path, baseline.Path);

        // Edits that keep every file: a file the save deletes comes back as a new file, which
        // TmdlFolderSync writes with LF newlines whatever the checkout uses.
        await Apply(session, m =>
        {
            m.RemoveObject(Remove("Sales/Total Sales"));
            m.SetProperty(Set("Customers", "description", "People"));
        });
        await Save(session);
        Assert.False(session.IsDirty);

        await session.UndoAsync("shell", None);
        Assert.True(session.IsDirty);
        await Save(session);

        TomLiveModelSessionTests.AssertSameFiles(baseline.Path, dir.Path);
    }

    [Fact]
    public async Task UndoingACascadingRemove_RestoresWhatTheRemoveTookWithIt()
    {
        await using var session = OpenRich();
        var original = Tmdl(session);

        await Apply(session, m => m.RemoveObject(Remove("Sales/Revenue")));
        await Apply(session, m => m.RemoveObject(Remove("Customer")));
        var removed = Tmdl(session);
        // The removes took the measure's perspective membership, translation, annotation and
        // KPI, and the table's relationship.
        Assert.DoesNotContain("Omsætning", removed);
        Assert.DoesNotContain("SalesToCustomer", removed);
        Assert.DoesNotContain("perspectiveMeasure Revenue", removed);
        Assert.Contains("perspectiveMeasure Revenue", original);

        await session.UndoAsync("shell", None);
        await session.UndoAsync("shell", None);

        Assert.Equal(original, Tmdl(session));
    }

    [Fact]
    public async Task UndoAndRedo_PublishInverseBatches_AndKeepObjectIds()
    {
        await using var session = OpenRich();
        var batches = new List<ModelChangeBatch>();
        session.Changed += (_, batch) => batches.Add(batch);
        var ids = Ids(await session.GetLiveSnapshotAsync(None));

        await Apply(session, m =>
        {
            m.SetProperty(Set("Sales/Revenue", "name", "Turnover"));
            m.AddObject(Add("Sales/Profit", "Measure", "1"));
            m.RemoveObject(Remove("Sales/Count"));
        });
        var applied = batches[^1];
        var afterIds = Ids(await session.GetLiveSnapshotAsync(None));

        var undo = await session.UndoAsync("mcp-1", None);
        Assert.NotNull(undo);
        Assert.Equal(new ChangeOrigin("mcp-1", ChangeOriginKind.Undo), undo.Origin);
        Assert.Equal(2, undo.Version);
        Assert.Equal(Describe(applied.Changes.Reverse().Select(c => c.Change switch
        {
            ModelChangeKind.Added => c with { Change = ModelChangeKind.Removed },
            ModelChangeKind.Removed => c with { Change = ModelChangeKind.Added },
            ModelChangeKind.Renamed => c with { Path = c.OldPath!, OldPath = c.Path },
            _ => c
        })), Describe(undo.Changes));
        Assert.Equal(ids, Ids(await session.GetLiveSnapshotAsync(None)));

        var redo = await session.RedoAsync("mcp-1", None);
        Assert.NotNull(redo);
        Assert.Equal(ChangeOriginKind.Redo, redo.Origin.Kind);
        Assert.Equal(3, redo.Version);
        Assert.Equal(Describe(applied.Changes), Describe(redo.Changes));
        Assert.Equal(afterIds, Ids(await session.GetLiveSnapshotAsync(None)));
        Assert.Equal([applied, undo, redo], batches);
    }

    [Fact]
    public async Task Undo_KeepsTheIdsOfObjectsFirstSeenAfterTheEdit()
    {
        await using var session = OpenRich();
        // No snapshot yet: only the edited object and its ancestors have IDs when it commits.
        await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));
        var ids = Ids(await session.GetLiveSnapshotAsync(None));

        await session.UndoAsync("shell", None);

        Assert.Equal(ids, Ids(await session.GetLiveSnapshotAsync(None)));
    }

    [Fact]
    public async Task TheSessionIsCleanExactlyAtTheSavePoint()
    {
        await using var session = OpenRich();
        await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));
        await Save(session);
        await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", "2")));
        Assert.True(session.IsDirty);

        await session.UndoAsync("shell", None);
        Assert.False(session.IsDirty);
        Assert.Equal(SessionState.Clean, session.State);

        await session.UndoAsync("shell", None);
        Assert.True(session.IsDirty);
        await session.RedoAsync("shell", None);
        Assert.False(session.IsDirty);
        await session.RedoAsync("shell", None);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task ANewChange_ClearsRedo_AndNothingToUndoReturnsNull()
    {
        await using var session = OpenRich();
        Assert.Null(await session.UndoAsync("shell", None));
        Assert.Null(await session.RedoAsync("shell", None));

        await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));
        await session.UndoAsync("shell", None);
        Assert.True(session.CanRedo);

        await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", "2")));
        Assert.False(session.CanRedo);
        Assert.Null(await session.RedoAsync("shell", None));

        // An empty commit is not a step and keeps the redo stack.
        await session.UndoAsync("shell", None);
        await Apply(session, _ => { });
        Assert.True(session.CanRedo);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task TheUndoLimit_DropsTheOldestSteps()
    {
        await using var session = OpenRich(undoLimit: 2);
        for (var i = 1; i <= 3; i++)
        {
            var value = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Apply(session, m => m.SetProperty(Set("Sales/Revenue", "expression", value)));
        }

        Assert.NotNull(await session.UndoAsync("shell", None));
        Assert.NotNull(await session.UndoAsync("shell", None));
        Assert.Null(await session.UndoAsync("shell", None));
        Assert.Equal("1", await Expression(session, "Sales/Revenue"));
    }

    [Fact]
    public async Task UndoInsideALease_Throws()
    {
        await using var session = OpenRich();
        await using var lease = await session.LeaseAsync(Shell, None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoAsync("shell", None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RedoAsync("shell", None));
    }

    [Fact]
    public async Task AnExplicitTransaction_GroupsTheClientsRequestsIntoOneStep_WhileOtherClientsWait()
    {
        await using var session = OpenRich();
        var batches = new List<ModelChangeBatch>();
        session.Changed += (_, batch) => batches.Add(batch);

        var transaction = await session.BeginTransactionAsync(Shell, None);
        var snapshotBefore = await session.GetLiveSnapshotAsync(None);

        // The client's requests come from flows that hold no lease, as separate requests do.
        await Request(session, Shell, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));
        await Request(session, Shell, m => m.AddObject(Add("Sales/Profit", "Measure", "2")));

        var otherStarted = false;
        var other = Request(session, new LiveLeaseOptions("mcp-1"), m =>
        {
            otherStarted = true;
            m.SetProperty(Set("Sales/Revenue", "description", "x"));
        });
        await Task.Delay(100);
        Assert.False(otherStarted);
        Assert.Empty(batches);
        // Other clients read the last committed version, without waiting.
        Assert.Same(snapshotBefore, await session.GetLiveSnapshotAsync(None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoAsync("shell", None));

        var batch = await transaction.CommitAsync(None);
        await other;

        Assert.NotNull(batch);
        Assert.Equal(transaction.Transaction, batch.Transaction);
        Assert.Contains(batch.Changes, c => c.Path == "Sales/Profit" && c.Change == ModelChangeKind.Added);
        Assert.Equal(2, session.Version);

        await session.UndoAsync("shell", None);
        await session.UndoAsync("shell", None);
        Assert.Equal("SUM(Sales[Amount])", await Expression(session, "Sales/Revenue"));
        Assert.Null(await Expression(session, "Sales/Profit"));
    }

    [Fact]
    public async Task RollingBackAnExplicitTransaction_DropsEveryRequestInIt()
    {
        await using var session = OpenRich();
        var original = Tmdl((TomLiveModelSession)session);

        var transaction = await session.BeginTransactionAsync(Shell, None);
        await Request(session, Shell, m => m.RemoveObject(Remove("Customer")));
        await Request(session, Shell, m => m.SetProperty(Set("Sales", "name", "Orders")));
        await transaction.RollbackAsync(None);

        Assert.Equal(original, Tmdl((TomLiveModelSession)session));
        Assert.Equal(0, session.Version);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task AFailedRequestInsideAnExplicitTransaction_RollsBackOnlyItself()
    {
        await using var session = OpenRich();
        var transaction = await session.BeginTransactionAsync(Shell, None);
        await Request(session, Shell, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Request(session, Shell, m =>
        {
            m.SetProperty(Set("Sales/Revenue", "expression", "2"));
            throw new InvalidOperationException("boom");
        }));

        await transaction.CommitAsync(None);
        Assert.Equal("1", await Expression(session, "Sales/Revenue"));
    }

    [Fact]
    public async Task ExplicitTransactions_DoNotNest()
    {
        await using var session = OpenRich();
        await using var transaction = await session.BeginTransactionAsync(Shell, None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginTransactionAsync(Shell, None));
        await using (await session.LeaseAsync(Shell, None))
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginTransactionAsync(new LiveLeaseOptions("other"), None));
    }

    [Fact]
    public async Task AnIdleExplicitTransaction_IsRolledBack()
    {
        await using var session = OpenRich(idleTimeout: TimeSpan.FromMilliseconds(200));
        var transaction = await session.BeginTransactionAsync(Shell, None);
        await Request(session, Shell, m => m.SetProperty(Set("Sales/Revenue", "expression", "1")));

        // Another client gets the model once the abandoned transaction times out.
        await Request(session, new LiveLeaseOptions("mcp-1"), _ => { }).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(None));
        Assert.Equal("SUM(Sales[Amount])", await Expression(session, "Sales/Revenue"));
    }

    [Fact]
    public async Task ClosingTheSession_RollsBackAnOpenExplicitTransaction()
    {
        var session = OpenRich();
        var transaction = await session.BeginTransactionAsync(Shell, None);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionState.Closed, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(None));
    }

    private static ILiveModelSession OpenRich(int undoLimit = TomLiveModelSession.DefaultUndoLimit, TimeSpan? idleTimeout = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tomix-rich-unused");
        return new TomLiveModelSession(TomModelSource.File(new ModelReference(folder), folder, "tmdl", _ => JournalFixture.Rich(), null))
        {
            UndoLimit = undoLimit,
            TransactionIdleTimeout = idleTimeout ?? Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Picks one edit of a random kind from what the model has.</summary>
    private static void RandomEdit(IModelSession session, ModelSnapshot snapshot, Random random, int i)
    {
        var mutator = (IModelMutationSession)session;
        var all = Flatten(snapshot.Objects).ToList();
        var tables = all.Where(o => o.Kind == ModelObjectKind.Table).ToList();
        var measures = all.Where(o => o.Kind == ModelObjectKind.Measure).ToList();
        var columns = all.Where(o => o.Kind is ModelObjectKind.Column or ModelObjectKind.CalculatedColumn).ToList();
        T Pick<T>(List<T> items) => items.Count > 0 ? items[random.Next(items.Count)] : throw new InvalidOperationException("Nothing to pick.");

        switch (random.Next(8))
        {
            case 0:
                mutator.SetProperty(Set(Pick(measures).Path, "expression", $"{i} + 1"));
                break;
            case 1:
                mutator.SetProperty(Set(Pick(all.Where(o => o.Kind is ModelObjectKind.Table or ModelObjectKind.Measure
                    or ModelObjectKind.Column).ToList()).Path, "description", $"edit {i}"));
                break;
            case 2:
                var measure = Pick(measures);
                mutator.SetProperty(Set(measure.Path, "name", $"{measure.Name} {i}"));
                break;
            case 3:
                mutator.AddObject(Add($"{Pick(tables).Name}/Probe {i}", "Measure", $"{i}"));
                break;
            case 4:
                mutator.RemoveObject(Remove(Pick(measures).Path));
                break;
            case 5:
                mutator.RemoveObject(Remove(Pick(columns).Path));
                break;
            case 6:
                ((IObjectMoveSession)session).MoveObject(Move(Pick(measures).Path, Pick(tables).Name));
                break;
            default:
                var table = Pick(tables);
                mutator.SetProperty(Set(table.Path, "name", $"{table.Name} {i}"));
                break;
        }
    }

    private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
        => objects.SelectMany(o => Flatten(o.Children).Prepend(o));

    private static string Tmdl(TomLiveModelSession session) => TmdlSerializer.SerializeDatabase(session.Journal.Database);

    private static string Tmdl(ILiveModelSession session) => Tmdl((TomLiveModelSession)session);

    private static async Task Apply(ILiveModelSession session, Action<IModelMutationSession> edit)
    {
        await using var lease = await session.LeaseAsync(Shell, None);
        edit((IModelMutationSession)lease.Session);
        await lease.CommitAsync(None);
    }

    /// <summary>One request of a client, from a flow of its own, as a host would run it.</summary>
    private static Task Request(ILiveModelSession session, LiveLeaseOptions options, Action<IModelMutationSession> edit)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(async () =>
            {
                await using var lease = await session.LeaseAsync(options, None);
                edit((IModelMutationSession)lease.Session);
                await lease.CommitAsync(None);
            });
        }
    }

    private static async Task Save(ILiveModelSession session)
    {
        await using var lease = await session.LeaseAsync(Shell, None);
        await ((IModelMutationSession)lease.Session).SaveAsync(null, "", overwrite: true, None);
        await lease.CommitAsync(None);
    }

    private static async Task<string?> Expression(ILiveModelSession session, string path)
        => Flatten((await session.GetLiveSnapshotAsync(None)).Snapshot.Objects).FirstOrDefault(o => o.Path == path)?.Expression;

    private static SortedDictionary<string, string> Ids(LiveModelSnapshot snapshot)
        => new(Flatten(snapshot.Snapshot.Objects).GroupBy(o => $"{o.Kind}:{o.Path}").ToDictionary(g => g.Key, g => g.First().Id!.ToString()!),
            StringComparer.Ordinal);

    private static List<string> Describe(IEnumerable<ModelChange> changes)
        => changes.Select(c => $"{c.Change} {c.ObjectKind} {c.Id} {c.Path} {c.OldPath}").ToList();
}
