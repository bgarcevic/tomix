using System.Data;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// A live session on a server model notices when the server's model changes outside it (#351):
/// it turns stale and refuses to save over the change until the caller keeps its own version,
/// and fails clearly once the server is gone. The server is a fake that commits like one.
/// </summary>
public sealed class TomLiveModelSessionServerSourceTests
{
    private static readonly LiveLeaseOptions Shell = new("shell");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_ClosedDesktop_FailsFastAsLocalInstanceGone(bool live)
    {
        // A port that was free a moment ago: nothing listens there, as after Desktop closes.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var provider = new TomServerModelProvider(tokenProvider: null);
        var reference = ModelReference.Remote($"localhost:{port}");

        var error = await Assert.ThrowsAsync<ModelConnectionException>(async () =>
        {
            if (live)
                await using (await provider.OpenLiveAsync(reference, CancellationToken.None)) { }
            else
                await using (await provider.OpenAsync(reference, CancellationToken.None)) { }
        });

        Assert.Equal(ModelConnectionFailureKind.LocalInstanceGone, error.Kind);
        Assert.Contains($"localhost:{port}", error.Message);
    }

    [Fact]
    public async Task AChangeOnTheServer_MakesTheSessionStale_AndASaveFailsWithoutReloadOffered()
    {
        var server = new FakeServer();
        await using var session = server.Open();

        server.CommitFromElsewhere();
        Assert.True(await session.CheckSourceAsync(CancellationToken.None));
        Assert.Equal(SessionState.Stale, session.State);
        Assert.False(session.CanReload);

        var ex = await Assert.ThrowsAsync<ModelSourceChangedException>(() => EditAndSaveAsync(session));
        Assert.False(ex.CanReload);
        Assert.Contains("on localhost:51234", ex.Message);
        Assert.Equal(0, server.Saves);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReloadAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task TheSessionsOwnSave_DoesNotMakeItStale_AndKeepingItsChangesSavesOverTheServer()
    {
        var server = new FakeServer();
        await using var session = server.Open();

        await EditAndSaveAsync(session);
        Assert.False(await session.CheckSourceAsync(CancellationToken.None));

        server.CommitFromElsewhere();
        await using (var lease = await session.LeaseAsync(Shell, CancellationToken.None))
        {
            ((IModelMutationSession)lease.Session).SetProperty(Set("Sales/Total Sales", "expression", "2"));
            ((IExternalChangeSession)lease.Session).KeepChanges();
            await ((IModelMutationSession)lease.Session).SaveAsync(null, "", overwrite: true, CancellationToken.None);
            await lease.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(2, server.Saves);
        Assert.Equal(SessionState.Clean, session.State);
    }

    [Fact]
    public async Task ARefreshTheSessionRuns_DoesNotMakeItStale_ButOneRunElsewhereDoes()
    {
        var server = new FakeServer();
        var session = server.OpenSession();
        await using var _ = session;

        await session.ChangeSourceAsync(() => Task.FromResult(server.CommitFromElsewhere()), CancellationToken.None);
        Assert.False(await session.CheckSourceAsync(CancellationToken.None));

        // Changed before the session's refresh: that change is not taken for the session's own.
        server.CommitFromElsewhere();
        await session.ChangeSourceAsync(() => Task.FromResult(server.CommitFromElsewhere()), CancellationToken.None);
        Assert.True(await session.CheckSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThePoll_ChecksTheServer_AndAServerThatIsGoneLeavesTheStateAlone()
    {
        var server = new FakeServer();
        await using var session = server.Open();
        var stale = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, change) => stale.TrySetResult(change.Current);

        Assert.False(session.SourceUnavailable);
        server.Gone = true;
        server.Poll();
        await Task.Delay(TomLiveModelSession.SourceSettleDelay * 4);
        Assert.Equal(SessionState.Clean, session.State);
        Assert.True(session.SourceUnavailable);

        server.Gone = false;
        server.CommitFromElsewhere();
        server.Poll();
        Assert.Equal(SessionState.Stale, await stale.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(session.SourceUnavailable);
    }

    [Fact]
    public async Task SavingOnceTheServerIsGone_FailsAsUnavailable()
    {
        var server = new FakeServer();
        await using var session = server.Open();
        server.Gone = true;

        var ex = await Assert.ThrowsAsync<ModelSourceUnavailableException>(() => EditAndSaveAsync(session));
        Assert.Contains("no longer running", ex.Message);
        Assert.True(session.SourceUnavailable);
    }

    [Fact]
    public void ATimedRowset_IsItsRowCountAndLatestChange()
    {
        var measures = new DataTable();
        measures.Columns.Add("ModifiedTime", typeof(DateTime));
        measures.Rows.Add(new DateTime(2026, 10, 5, 4, 35, 7));
        measures.Rows.Add(new DateTime(2026, 10, 6, 9, 0, 0));
        measures.Rows.Add(DBNull.Value);

        using var rows = measures.CreateDataReader();
        Assert.Equal("3|2026-10-06T09:00:00.0000000", TomServerModelSource.TimedPartOf(rows));
    }

    [Fact]
    public void AnUntimedRowset_IsItsContent_InAnyRowOrder()
    {
        static string Part(params (string Id, string Folder)[] groups)
        {
            var table = new DataTable();
            table.Columns.Add("ID", typeof(string));
            table.Columns.Add("Folder", typeof(string));
            foreach (var (id, folder) in groups)
                table.Rows.Add(id, folder);
            using var rows = table.CreateDataReader();
            return TomServerModelSource.ContentPartOf(rows);
        }

        Assert.Equal(Part(("1", "Sales"), ("2", "Finance")), Part(("2", "Finance"), ("1", "Sales")));
        Assert.NotEqual(Part(("1", "Sales"), ("2", "Finance")), Part(("1", "Sales"), ("2", "Budget")));
        Assert.NotEqual(Part(("1", "Sales")), Part(("1", "Sales"), ("2", "Finance")));
    }

    [Theory]
    [InlineData("localhost:51234", 10)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales", 30)]
    public void ALocalDesktop_IsPolledMoreOftenThanARemoteServer(string endpoint, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), TomServerModelSource.PollInterval(new ModelReference(endpoint)));

    [Theory]
    [InlineData("localhost:51234", false, true)]
    [InlineData("localhost:51234", true, false)]
    [InlineData("127.0.0.1:51234", false, true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales", false, false)]
    public void ALocalInstance_IsGoneWhenNothingListensOnItsPort(string endpoint, bool listening, bool gone)
        => Assert.Equal(gone, TomServerModelSource.LocalInstanceGone(endpoint, _ => listening));

    private static async Task EditAndSaveAsync(ILiveModelSession session)
    {
        await using var lease = await session.LeaseAsync(Shell, CancellationToken.None);
        var mutator = (IModelMutationSession)lease.Session;
        mutator.SetProperty(Set("Sales/Total Sales", "expression", "1"));
        await mutator.SaveAsync(null, "", overwrite: true, CancellationToken.None);
        await lease.CommitAsync(CancellationToken.None);
    }

    /// <summary>A server model: a commit, from the session or elsewhere, moves its fingerprint.</summary>
    private sealed class FakeServer
    {
        private int _commits;
        private Action? _poll;

        public int Saves { get; private set; }

        public bool Gone { get; set; }

        public int CommitFromElsewhere() => Interlocked.Increment(ref _commits);

        public void Poll() => _poll?.Invoke();

        public ILiveModelSession Open() => OpenSession();

        public TomLiveModelSession OpenSession()
        {
            using var dir = TomLiveModelSessionTests.CopyOfBasicTmdl();
            var database = TmdlSerializer.DeserializeDatabaseFromFolder(dir.Path);
            return new TomLiveModelSession(new Source(this, database));
        }

        private sealed class Source(FakeServer server, Database database)
            : TomModelSource(new ModelReference("localhost:51234", "Sales"), tokenProvider: null)
        {
            public override string SourcePath => "";

            public override string DisplayName => $"'Sales' on {Reference.Value}";

            public override TomCheckpointRestore Restore => TomCheckpointRestore.Swap;

            public override Database Load() => database;

            public override string ModelName(Database database) => "Sales";

            public override bool IsInPlace(string? outputPath) => true;

            public override string? Fingerprint()
                => server.Gone ? throw Unavailable() : Volatile.Read(ref server._commits).ToString(System.Globalization.CultureInfo.InvariantCulture);

            public override IDisposable? Watch(Action changed)
            {
                server._poll = changed;
                return null;
            }

            public override Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public override Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
            {
                if (server.Gone)
                    throw Unavailable();
                server.Saves++;
                server.CommitFromElsewhere();
                return Task.FromResult(new ModelExportResult("Sales", "remote"));
            }

            public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private ModelSourceUnavailableException Unavailable()
                => new(DisplayName, $"The Power BI Desktop instance at {Reference.Value} is no longer running.");
        }
    }
}
