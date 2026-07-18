using Tomix.App.Refresh;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class RefreshModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenRefreshTypeUnknown()
    {
        var handler = new RefreshModelHandler([new StubRefreshProvider(new StubRefreshSession())], () => null);
        var result = await handler.HandleAsync(
            Request(refreshType: "bogus"),
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_BAD_TYPE", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenBothTablesAndPartitionsPassed()
    {
        var handler = new RefreshModelHandler([new StubRefreshProvider(new StubRefreshSession())], () => null);
        var result = await handler.HandleAsync(
            Request(tables: ["Sales"], partitions: [new TablePartition("Sales", "Internet")]),
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_TABLE_PARTITION_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenPartitionValueMalformed()
    {
        var handler = new RefreshModelHandler([new StubRefreshProvider(new StubRefreshSession())], () => null);
        var result = await handler.HandleAsync(
            Request(partitions: [new TablePartition("Sales", "")]),
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_BAD_PARTITION", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task ResolveTarget_PicksPrimary_WhenPrimaryIsRemote()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"));
            var resolver = new ActiveModelResolver(store);
            var request = Request(server: null, database: null);

            var target = RefreshModelHandler.ResolveTarget(request, resolver);

            Assert.NotNull(target);
            Assert.True(target!.IsRemote);
            Assert.Equal("MyModel", target.Database);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveTarget_PicksSecondary_WhenPrimaryIsLocalAndSecondaryIsRemote()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CliStateStore(dir);
            // Primary local (Model path set), Workspace holds a remote endpoint => the secondary.
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: "MyModel",
                Model: "./my-model.tmdl",
                Auth: null,
                Local: true,
                Profile: null,
                Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws",
                WorkspaceFormat: null,
                WorkspaceAuth: null));
            var resolver = new ActiveModelResolver(store);
            var request = Request(server: null, database: null);

            var target = RefreshModelHandler.ResolveTarget(request, resolver);

            Assert.NotNull(target);
            Assert.True(target!.IsRemote);
            Assert.Equal("MyModel", target.Database);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenPrimaryLocalAndNoSecondary()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(LocalSession());
            var resolver = new ActiveModelResolver(store);
            var request = Request();

            var target = RefreshModelHandler.ResolveTarget(request, resolver);

            Assert.Null(target);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenSecondaryIsAlsoLocal()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: "MyModel",
                Model: "./my-model.tmdl",
                Auth: null,
                Local: true,
                Profile: null,
                Workspace: "./mirror", // local path, not remote
                WorkspaceFormat: "tmdl",
                WorkspaceAuth: null));
            var resolver = new ActiveModelResolver(store);
            var request = Request();

            var target = RefreshModelHandler.ResolveTarget(request, resolver);

            Assert.Null(target);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HandleAsync_DryRun_ReturnsScriptWithoutExecuting()
    {
        var session = new StubRefreshSession();
        var handler = new RefreshModelHandler(
            [new StubRefreshProvider(session)],
            () => RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"));
        var result = await handler.HandleAsync(
            Request(dryRun: true, database: "MyModel"),
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data!.Script);
        Assert.Contains("\"refresh\"", result.Data.Script);
        Assert.Contains("full", result.Data.Script);
        Assert.False(session.RefreshCalled);
    }

    [Fact]
    public async Task HandleAsync_ReturnsUnsupported_WhenSessionIsNotRefreshCapable()
    {
        var handler = new RefreshModelHandler(
            [new StubNonRefreshProvider()],
            () => RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"));
        var result = await handler.HandleAsync(
            Request(database: "MyModel"),
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    private static RefreshModelRequest Request(
        string refreshType = "full",
        string[]? tables = null,
        TablePartition[]? partitions = null,
        bool dryRun = false,
        string? server = null,
        string? database = null) =>
        new(Model: null,
            Server: server,
            Database: database,
            Auth: null,
            RefreshType: refreshType,
            Tables: tables,
            Partitions: partitions,
            ApplyRefreshPolicy: true,
            EffectiveDate: null,
            MaxParallelism: null,
            DryRun: dryRun,
            NoProgress: false,
            TracePath: null);

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"tomix-refresh-test-{Guid.NewGuid():N}");

    private static CliConnectionState RemoteSession(string endpoint, string database) =>
        new(Server: endpoint,
            Database: database,
            Model: null,
            Auth: null,
            Local: false,
            Profile: null,
            Workspace: null);

    private static CliConnectionState LocalSession() =>
        new(Server: null,
            Database: null,
            Model: "./my-model.tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: null);

    private sealed class StubRefreshProvider : IModelProvider
    {
        private readonly StubRefreshSession _session;
        public StubRefreshProvider(StubRefreshSession session) => _session = session;
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class StubRefreshSession : IModelSession, IModelRefreshSession
    {
        public bool RefreshCalled { get; private set; }
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelRefreshResult> RefreshAsync(
            ModelRefreshRequest request,
            IProgress<RefreshProgress>? progress,
            TextWriter? traceWriter,
            CancellationToken cancellationToken)
        {
            RefreshCalled = true;
            return Task.FromResult(new ModelRefreshResult(
                "stub-server",
                request.Database ?? "stub",
                request.RefreshType,
                DurationMs: 1,
                Tables: [new RefreshTableResult("Sales", 100, 5, 5, 10)],
                Totals: new RefreshTableResult("Total", 100, 5, 5, 10)));
        }

        public string GenerateRefreshScript(ModelRefreshRequest request) =>
            "{\"database\":\"stub\",\"refresh\":{\"type\":\"" + request.RefreshType + "\"}}";
    }

    private sealed class StubNonRefreshProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubNonRefreshSession());
    }

    private sealed class StubNonRefreshSession : IModelSession
    {
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
