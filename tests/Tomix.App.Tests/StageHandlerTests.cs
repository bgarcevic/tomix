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
        var configDir = Path.Combine(Path.GetTempPath(), $"tomix-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            var handler = new StageHandler(new StagingStore(configDir, "test-session"));
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
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public void Discard_All_SucceedsWithoutAModel()
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"tomix-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            var handler = new StageHandler(new StagingStore(configDir, "test-session"));

            var result = handler.Discard(new ModelReference(""), all: true);

            Assert.True(result.Success);
            Assert.Equal(0, result.Data!.Discarded);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public async Task CommitAsync_ReturnsFail_WhenNothingStaged()
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"tomix-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            var staging = new StagingStore(configDir, "test-session");
            var handler = new StageHandler(staging);
            var result = await handler.CommitAsync(
                new ModelReference("./nonexistent.tmdl"),
                [new StubLocalProvider()],
                force: false,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("TOMIX_STAGE_NOTHING_TO_COMMIT", result.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public async Task CommitAsync_DeploysToRemote_WhenSourceKindIsRemote()
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"tomix-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            var staging = new StagingStore(configDir, "test-session");
            var source = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel");
            var workingDir = Path.Combine(configDir, "working");
            Directory.CreateDirectory(workingDir);

            var manifest = new StagingManifest(
                SessionId: "test-session",
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

            staging.WriteManifest(source, manifest);

            var handler = new StageHandler(staging);
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
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    [Fact]
    public async Task CommitAsync_ExportsLocally_WhenSourceKindIsLocal()
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"tomix-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            var staging = new StagingStore(configDir, "test-session");

            var sourcePath = Path.Combine(configDir, "source-model");
            Directory.CreateDirectory(sourcePath);

            var workingDir = Path.Combine(configDir, "working");
            Directory.CreateDirectory(workingDir);

            var source = new ModelReference(sourcePath);
            var manifest = new StagingManifest(
                SessionId: "test-session",
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

            staging.WriteManifest(source, manifest);

            var handler = new StageHandler(staging);
            var result = await handler.CommitAsync(
                source,
                [new StubExportProvider(workingDir, sourcePath)],
                force: false,
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Data!.RemoteDeployed);
            Assert.Equal(1, result.Data.OpsCommitted);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    private sealed class StubDeployProvider : IModelProvider
    {
        private readonly string _expectedPath;

        public StubDeployProvider(string expectedPath) => _expectedPath = expectedPath;

        public bool CanOpen(ModelReference reference)
            => reference.Value == _expectedPath || Directory.Exists(_expectedPath);

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubDeploySession());
    }

    private sealed class StubDeploySession : IModelSession, IModelDeploySession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");
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
