using Tomix.App.Save;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class SaveModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_SyncsToWorkspace_WhenSyncTargetSet()
    {
        var exportDir = Path.Combine(Path.GetTempPath(), $"tomix-save-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDir);
        try
        {
            var handler = new SaveModelHandler([new StubSaveProvider(exportDir, deploySucceeds: true)]);
            var result = await handler.HandleAsync(
                new SaveModelRequest(
                    Model: new ModelReference(exportDir),
                    OutputPath: exportDir,
                    Serialization: "tmdl",
                    Force: true,
                    SupportingFiles: false,
                    SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Data!.Synced);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws / MyModel", result.Data.SyncTarget);
            Assert.Null(result.Data.SyncWarning);
        }
        finally
        {
            Directory.Delete(exportDir, true);
        }
    }

    [Fact]
    public async Task HandleAsync_SetsWarning_WhenSyncFails()
    {
        var exportDir = Path.Combine(Path.GetTempPath(), $"tomix-save-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDir);
        try
        {
            var handler = new SaveModelHandler([new StubSaveProvider(exportDir, deploySucceeds: false)]);
            var result = await handler.HandleAsync(
                new SaveModelRequest(
                    Model: new ModelReference(exportDir),
                    OutputPath: exportDir,
                    Serialization: "tmdl",
                    Force: true,
                    SupportingFiles: false,
                    SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Data!.Synced);
            Assert.NotNull(result.Data.SyncWarning);
            Assert.Contains("sync failed", result.Data.SyncWarning, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(exportDir, true);
        }
    }

    [Fact]
    public async Task HandleAsync_SkipsSync_WhenNoSyncTarget()
    {
        var exportDir = Path.Combine(Path.GetTempPath(), $"tomix-save-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDir);
        try
        {
            var handler = new SaveModelHandler([new StubSaveProvider(exportDir, deploySucceeds: true)]);
            var result = await handler.HandleAsync(
                new SaveModelRequest(
                    Model: new ModelReference(exportDir),
                    OutputPath: exportDir,
                    Serialization: "tmdl",
                    Force: true,
                    SupportingFiles: false),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Data!.Synced);
            Assert.Null(result.Data.SyncTarget);
            Assert.Null(result.Data.SyncWarning);
        }
        finally
        {
            Directory.Delete(exportDir, true);
        }
    }

    [Fact]
    public async Task HandleAsync_SetsWarning_WhenSessionCannotDeploy()
    {
        var exportDir = Path.Combine(Path.GetTempPath(), $"tomix-save-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDir);
        try
        {
            var handler = new SaveModelHandler([new StubExportOnlyProvider(exportDir)]);
            var result = await handler.HandleAsync(
                new SaveModelRequest(
                    Model: new ModelReference(exportDir),
                    OutputPath: exportDir,
                    Serialization: "tmdl",
                    Force: true,
                    SupportingFiles: false,
                    SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Data!.Synced);
            Assert.NotNull(result.Data.SyncWarning);
            Assert.Contains("does not support deploy", result.Data.SyncWarning, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(exportDir, true);
        }
    }

    private sealed class StubSaveProvider : IModelProvider
    {
        private readonly string _exportDir;
        private readonly bool _deploySucceeds;

        public StubSaveProvider(string exportDir, bool deploySucceeds)
        {
            _exportDir = exportDir;
            _deploySucceeds = deploySucceeds;
        }

        public bool CanOpen(ModelReference reference) => reference.Value == _exportDir;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSaveSession(_exportDir, _deploySucceeds));
    }

    private sealed class StubSaveSession : IModelSession, IModelExportSession, IModelDeploySession
    {
        private readonly string _exportDir;
        private readonly bool _deploySucceeds;

        public StubSaveSession(string exportDir, bool deploySucceeds)
        {
            _exportDir = exportDir;
            _deploySucceeds = deploySucceeds;
        }

        public string SourcePath => _exportDir;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(_exportDir, request.Serialization));

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            if (_deploySucceeds)
                return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));

            throw new InvalidOperationException("Deploy failed for test purposes.");
        }

        public string GenerateScript(ModelDeployRequest request) => "";
    }

    private sealed class StubExportOnlyProvider : IModelProvider
    {
        private readonly string _exportDir;

        public StubExportOnlyProvider(string exportDir) => _exportDir = exportDir;

        public bool CanOpen(ModelReference reference) => reference.Value == _exportDir;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubExportOnlySession(_exportDir));
    }

    private sealed class StubExportOnlySession : IModelSession, IModelExportSession
    {
        private readonly string _exportDir;

        public StubExportOnlySession(string exportDir) => _exportDir = exportDir;

        public string SourcePath => _exportDir;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(_exportDir, request.Serialization));
    }
}
