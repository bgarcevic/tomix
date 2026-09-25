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
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"));
        var resolver = new ActiveModelResolver(config.State);
        var request = Request(server: null, database: null);

        var target = RefreshModelHandler.ResolveTarget(request, resolver);

        Assert.NotNull(target);
        Assert.True(target!.IsRemote);
        Assert.Equal("MyModel", target.Database);
    }

    [Fact]
    public void ResolveTarget_PicksSecondary_WhenPrimaryIsLocalAndSecondaryIsRemote()
    {
        using var config = new TempConfigDir();
        // Primary local (Model path set), Workspace holds a remote endpoint => the secondary.
        config.State.SaveCurrentSession(new CliConnectionState(
            Server: null,
            Database: "MyModel",
            Model: "./my-model.tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            WorkspaceFormat: null,
            WorkspaceAuth: null));
        var resolver = new ActiveModelResolver(config.State);
        var request = Request(server: null, database: null);

        var target = RefreshModelHandler.ResolveTarget(request, resolver);

        Assert.NotNull(target);
        Assert.True(target!.IsRemote);
        Assert.Equal("MyModel", target.Database);
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenExplicitLocalModelIsNotTheSessionPrimary()
    {
        // An explicit unrelated local source must not fall back to refreshing the
        // session's workspace mirror (#134).
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(new CliConnectionState(
            Server: null,
            Database: "MyModel",
            Model: "./my-model.tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            WorkspaceFormat: null,
            WorkspaceAuth: null));
        var resolver = new ActiveModelResolver(config.State);
        var request = Request(model: Path.Combine(Path.GetTempPath(), "unrelated.tmdl"));

        var target = RefreshModelHandler.ResolveTarget(request, resolver);

        Assert.Null(target);
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenPrimaryLocalAndNoSecondary()
    {
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(LocalSession());
        var resolver = new ActiveModelResolver(config.State);
        var request = Request();

        var target = RefreshModelHandler.ResolveTarget(request, resolver);

        Assert.Null(target);
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenSecondaryIsAlsoLocal()
    {
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(new CliConnectionState(
            Server: null,
            Database: "MyModel",
            Model: "./my-model.tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "./mirror", // local path, not remote
            WorkspaceFormat: "tmdl",
            WorkspaceAuth: null));
        var resolver = new ActiveModelResolver(config.State);
        var request = Request();

        var target = RefreshModelHandler.ResolveTarget(request, resolver);

        Assert.Null(target);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ReturnsUnsupported_WhenSessionIsNotRefreshCapable(bool policyOnly)
    {
        var handler = new RefreshModelHandler(
            [new StubNonRefreshProvider()],
            () => RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"));
        var result = await handler.HandleAsync(
            Request(refreshType: "automatic", database: "MyModel", tables: ["Sales"]) with { PolicyOnly = policyOnly },
            progress: null,
            traceWriter: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(policyOnly ? "TOMIX_REFRESH_POLICY_UNSUPPORTED" : "TOMIX_REFRESH_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyOnly_UsesApplyWithoutLoading_AndDryRunNeverExecutes(bool dryRun)
    {
        var session = new StubRefreshSession();
        var request = Request(refreshType: "automatic", tables: ["Sales"], dryRun: dryRun,
            server: "powerbi://api.powerbi.com/v1.0/myorg/ws", database: "Model") with
        { PolicyOnly = true, EffectiveDate = new DateOnly(2024, 6, 1), MaxParallelism = 4 };
        var result = await new RefreshModelHandler([new StubRefreshProvider(session)], () => null)
            .HandleAsync(request, null, null, CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.False(session.RefreshCalled);
        Assert.Null(result.Data!.Script);
        if (dryRun)
        {
            Assert.Null(session.Applied);
            Assert.NotNull(result.Data.PolicyPreview);
            Assert.Equal(request.EffectiveDate, result.Data.PolicyPreview!.EffectiveDate);
        }
        else
        {
            Assert.NotNull(session.Applied);
            Assert.False(session.Applied!.Refresh);
            Assert.Equal(request.EffectiveDate, session.Applied.EffectiveDate);
            Assert.Equal(4, session.Applied.MaxParallelism);
            Assert.Equal(["created partition '2024'"], result.Data.PolicyApplication!.Operations);
        }
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("multiple-tables")]
    [InlineData("partition")]
    [InlineData("refresh-type")]
    [InlineData("skip-policy")]
    [InlineData("trace")]
    [InlineData("parallelism")]
    public async Task PolicyOnly_RejectsConflictsBeforeOpeningProvider(string scenario)
    {
        var request = Request(refreshType: "automatic", tables: ["Sales"]) with { PolicyOnly = true };
        request = scenario switch
        {
            "missing-table" => request with { Tables = null },
            "multiple-tables" => request with { Tables = ["Sales", "Other"] },
            "partition" => request with { Partitions = [new("Sales", "P")] },
            "refresh-type" => request with { RefreshTypeExplicit = true },
            "skip-policy" => request with { ApplyRefreshPolicy = false },
            "trace" => request with { TracePath = "trace.log" },
            _ => request with { MaxParallelism = 0 }
        };
        var result = await new RefreshModelHandler([], () => null).HandleAsync(request, null, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task PolicyOnly_LocalModelWithoutMirrorFails()
    {
        var session = new StubRefreshSession();
        var result = await new RefreshModelHandler([new StubRefreshProvider(session)], LocalSession)
            .HandleAsync(Request(refreshType: "automatic", tables: ["Sales"]) with { PolicyOnly = true },
                null, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_NO_REMOTE_TARGET", result.Diagnostics[0].Code);
        Assert.Null(session.Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyOnly_MissingPolicyNeverApplies(bool dryRun)
    {
        var session = new StubRefreshSession { MissingPolicy = true };
        var result = await new RefreshModelHandler([new StubRefreshProvider(session)],
            () => RemoteSession("powerbi://api.powerbi.com/v1.0/myorg/ws", "Model"))
            .HandleAsync(Request(refreshType: "automatic", tables: ["Sales"], dryRun: dryRun) with { PolicyOnly = true },
                null, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Null(session.Applied);
        Assert.False(session.RefreshCalled);
    }

    [Fact]
    public async Task PolicyOnly_UsesRemoteWorkspaceMirror()
    {
        var session = new StubRefreshSession();
        var connection = LocalSession() with { Workspace = "powerbi://api.powerbi.com/v1.0/myorg/ws", Database = "Model" };
        var result = await new RefreshModelHandler([new StubRefreshProvider(session)], () => connection)
            .HandleAsync(Request(refreshType: "automatic", tables: ["Sales"]) with { PolicyOnly = true },
                null, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.NotNull(session.Applied);
    }

    private static RefreshModelRequest Request(
        string refreshType = "full",
        string[]? tables = null,
        TablePartition[]? partitions = null,
        bool dryRun = false,
        string? server = null,
        string? database = null,
        string? model = null) =>
        new(Model: model,
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

    private sealed class StubRefreshSession : IModelSession, IModelRefreshSession, IRefreshPolicyApplySession, IRefreshPolicyMutationSession
    {
        public RefreshPolicyApplyRequest? Applied { get; private set; }
        public bool MissingPolicy { get; init; }
        public RefreshPolicyInfo? GetRefreshPolicy(string table) => MissingPolicy ? null
            : new(table, "Import", "Year", 10, "Day", 3, 0, "", "RangeStart RangeEnd", [], []);
        public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false) => throw new NotSupportedException();
        public Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(RefreshPolicyApplyRequest request, CancellationToken cancellationToken)
        {
            Applied = request;
            return Task.FromResult(new RefreshPolicyApplyResult("server", "Model", request.Table,
                request.EffectiveDate!.Value, request.Refresh, ["created partition '2024'"], 1));
        }
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
