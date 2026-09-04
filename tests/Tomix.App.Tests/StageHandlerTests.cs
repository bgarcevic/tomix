using Tomix.App.Stage;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class StageHandlerTests
{
    // With no active session the resolver produces an empty reference; every stage
    // command taking a source must fail with TOMIX_NO_MODEL, not crash in StagingStore.
    [Fact]
    public async Task EmptyReference_FailsWithNoModel_InsteadOfCrashing()
    {
        using var config = new TempConfigDir();
        var handler = new StageHandler(config.Staging);
        var empty = new ModelReference("");

        var status = handler.Status(empty);
        var discard = handler.Discard(empty, all: false);
        var commit = await handler.CommitAsync(empty, [], force: false, CancellationToken.None);

        foreach (var (success, diagnostics, exitCode) in new[]
        {
            (status.Success, status.Diagnostics, status.ExitCode),
            (discard.Success, discard.Diagnostics, discard.ExitCode),
            (commit.Success, commit.Diagnostics, commit.ExitCode),
        })
        {
            Assert.False(success);
            Assert.Equal("TOMIX_NO_MODEL", diagnostics[0].Code);
            Assert.Equal(2, exitCode);
        }
    }

    [Fact]
    public void Discard_All_SucceedsWithoutAModel()
    {
        using var config = new TempConfigDir();
        var handler = new StageHandler(config.Staging);

        var result = handler.Discard(new ModelReference(""), all: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.Data!.Discarded);
    }

    [Fact]
    public async Task CommitAsync_ReturnsFail_WhenNothingStaged()
    {
        using var config = new TempConfigDir();
        var handler = new StageHandler(config.Staging);

        var result = await handler.CommitAsync(
            new ModelReference("./nonexistent.tmdl"),
            [new StubLocalProvider()],
            force: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_NOTHING_TO_COMMIT", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task CommitAsync_DeploysToRemote_WhenSourceKindIsRemote()
    {
        using var config = new TempConfigDir();
        var source = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel");
        var workingDir = config.CreateSubdirectory("working");

        var manifest = new StagingManifest(
            SessionId: TempConfigDir.SessionId,
            Source: $"powerbi://api.powerbi.com/v1.0/myorg/ws|MyModel",
            SourceKind: "remote",
            SourceEndpoint: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            SourceDatabase: "MyModel",
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: workingDir,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: [new StagedOp(1, DateTimeOffset.UtcNow, "add table", "Added table X")]);

        config.Staging.WriteManifest(source, manifest);

        var handler = new StageHandler(config.Staging);
        var result = await handler.CommitAsync(
            source,
            [new StubDeployProvider(workingDir)],
            force: false,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.RemoteDeployed);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", result.Data.Server);
        Assert.Equal("MyModel", result.Data.Database);
        Assert.NotNull(result.Data.DeployDurationMs);
        Assert.Equal(1, result.Data.OpsCommitted);
    }

    /// <summary>
    /// A staged commit pushes the whole working copy to the remote, so it carries the same
    /// data-loss risk as a synced save (issue #129): deploying incremental-refresh policy
    /// partitions discards their processed data on the service. Pinned in both directions — the
    /// exemption drops only <c>DeployPolicyPartitions</c>, and a staged <c>incremental-refresh</c>
    /// op restores the full deploy so the policy edit actually lands.
    /// </summary>
    [Theory]
    [InlineData(new[] { "add" }, false)]
    [InlineData(new[] { "add", "set" }, false)]
    [InlineData(new[] { "incremental-refresh" }, true)]
    [InlineData(new[] { "add", "incremental-refresh" }, true)]
    public async Task CommitAsync_RemoteSync_PreservesPolicyPartitions_UnlessRefreshPolicyStaged(
        string[] commands, bool deploysPolicyPartitions)
    {
        using var config = new TempConfigDir();
        var source = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel");
        var workingDir = config.CreateSubdirectory("working");

        var manifest = new StagingManifest(
            SessionId: TempConfigDir.SessionId,
            Source: "powerbi://api.powerbi.com/v1.0/myorg/ws|MyModel",
            SourceKind: "remote",
            SourceEndpoint: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            SourceDatabase: "MyModel",
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: workingDir,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: [.. commands.Select((c, i) => new StagedOp(i + 1, DateTimeOffset.UtcNow, c, $"{c} X"))]);

        config.Staging.WriteManifest(source, manifest);

        var provider = new StubDeployProvider(workingDir);
        var handler = new StageHandler(config.Staging);
        var result = await handler.CommitAsync(source, [provider], force: false, CancellationToken.None);

        Assert.True(result.Success);
        var expected = deploysPolicyPartitions
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };
        Assert.Equal(expected, provider.Session.LastDeployRequest?.EffectiveOptions);
    }

    [Fact]
    public async Task CommitAsync_ExportsLocally_WhenSourceKindIsLocal()
    {
        using var config = new TempConfigDir();
        var sourcePath = config.CreateSubdirectory("source-model");
        var workingDir = config.CreateSubdirectory("working");

        var source = new ModelReference(sourcePath);
        var manifest = new StagingManifest(
            SessionId: TempConfigDir.SessionId,
            Source: sourcePath,
            SourceKind: "local",
            SourceEndpoint: null,
            SourceDatabase: null,
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: workingDir,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: [new StagedOp(1, DateTimeOffset.UtcNow, "add measure", "Added measure X")]);

        config.Staging.WriteManifest(source, manifest);

        var handler = new StageHandler(config.Staging);
        var result = await handler.CommitAsync(
            source,
            [new StubExportProvider(workingDir, sourcePath)],
            force: false,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.RemoteDeployed);
        Assert.Equal(1, result.Data.OpsCommitted);
    }

    private sealed class StubDeployProvider : IModelProvider
    {
        private readonly string _expectedPath;

        public StubDeployProvider(string expectedPath) => _expectedPath = expectedPath;

        public StubDeploySession Session { get; } = new();

        public bool CanOpen(ModelReference reference)
            => reference.Value == _expectedPath || Directory.Exists(_expectedPath);

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(Session);
    }

    private sealed class StubDeploySession : IModelSession, IModelDeploySession
    {
        public ModelDeployRequest? LastDeployRequest { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            LastDeployRequest = request;
            return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));
        }

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This stub never plans a deploy.");
    }

    private sealed class StubExportProvider : IModelProvider
    {
        private readonly string _expectedPath;
        private readonly string _exportTarget;

        public StubExportProvider(string expectedPath, string exportTarget)
        {
            _expectedPath = expectedPath;
            _exportTarget = exportTarget;
        }

        public bool CanOpen(ModelReference reference) => reference.Value == _expectedPath;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubExportSession(_exportTarget));
    }

    private sealed class StubExportSession : IModelSession, IModelExportSession
    {
        private readonly string _exportTarget;

        public StubExportSession(string exportTarget) => _exportTarget = exportTarget;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(_exportTarget, request.Serialization));
    }

    private sealed class StubLocalProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => false;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct) => throw new NotSupportedException();
    }
}
