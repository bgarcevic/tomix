using Tomix.App.Mutations;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class MutationLifecycleSyncTests
{
    private static readonly ModelReference SyncTarget =
        new("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel");

    [Fact]
    public async Task CompleteAsync_SyncsToWorkspace_WhenSyncTargetSet()
    {
        var context = NewSaveContext(SyncTarget);

        var outcome = await MutationLifecycle.CompleteAsync(
            new StubMutationSession(deploySucceeds: true),
            context, "add", "add X", CancellationToken.None);

        Assert.True(outcome.Synced);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws / MyModel", outcome.SyncTarget);
        Assert.Null(outcome.SyncWarning);
    }

    [Fact]
    public async Task CompleteAsync_SetsWarning_WhenDeployFails()
    {
        var context = NewSaveContext(SyncTarget);

        var outcome = await MutationLifecycle.CompleteAsync(
            new StubMutationSession(deploySucceeds: false),
            context, "add", "add X", CancellationToken.None);

        Assert.False(outcome.Synced);
        Assert.Contains("sync failed", outcome.SyncWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_SkipsSync_WhenNoSyncTarget()
    {
        var context = NewSaveContext(syncTarget: null);

        var outcome = await MutationLifecycle.CompleteAsync(
            new StubMutationSession(deploySucceeds: true),
            context, "add", "add X", CancellationToken.None);

        Assert.False(outcome.Synced);
        Assert.Null(outcome.SyncTarget);
        Assert.Null(outcome.SyncWarning);
    }

    [Fact]
    public async Task CompleteAsync_SkipsSync_WhenSessionCannotDeploy()
    {
        var context = NewSaveContext(SyncTarget);

        var outcome = await MutationLifecycle.CompleteAsync(
            new StubNonDeployMutationSession(),
            context, "add", "add X", CancellationToken.None);

        Assert.False(outcome.Synced);
        Assert.Contains("does not support deploy", outcome.SyncWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_SyncFailure_MarksOutcomeAsFailed()
    {
        var context = NewSaveContext(SyncTarget);

        var failed = await MutationLifecycle.CompleteAsync(
            new StubMutationSession(deploySucceeds: false),
            context, "add", "add X", CancellationToken.None);
        var succeeded = await MutationLifecycle.CompleteAsync(
            new StubMutationSession(deploySucceeds: true),
            context, "add", "add X", CancellationToken.None);

        // SyncFailed drives the non-zero exit code so CI catches mirror drift.
        Assert.True(failed.SyncFailed);
        Assert.False(succeeded.SyncFailed);
    }

    [Fact]
    public async Task SyncAsync_ReportsProgress_AndRecoveryHint()
    {
        var messages = new List<string>();
        using var _ = MutationProgress.Use(messages.Add);

        var (synced, target, warning) = await WorkspaceSync.SyncAsync(
            new StubMutationSession(deploySucceeds: false),
            SyncTarget, force: false, CancellationToken.None);

        Assert.False(synced);
        Assert.Contains(messages, m => m.StartsWith("Syncing to powerbi://", StringComparison.Ordinal));
        Assert.Contains("--no-sync", warning);
    }

    [Fact]
    public async Task SyncAsync_PropagatesCancellation_InsteadOfWarning()
    {
        // Ctrl-C during a sync must reach the top-level exit-130 handler, not be
        // downgraded to a "sync failed" warning.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WorkspaceSync.SyncAsync(
                new CancellingDeploySession(), SyncTarget, force: false, CancellationToken.None));
    }

    [Fact]
    public async Task BeginAsync_ResolvesSyncTarget_ForInPlaceSave()
    {
        var begin = await MutationLifecycle.BeginAsync(
            [], new ModelReference("/local/model"),
            new MutationOptions(Save: true, SaveTo: null, Stage: false, Revert: false, Serialization: "", Force: false),
            new Tomix.App.State.StagingStore(), WorkspaceConnection(), CancellationToken.None);

        Assert.NotNull(begin.Context!.SyncTarget);
    }

    [Fact]
    public async Task BeginAsync_SkipsSyncTarget_WhenSavingToASideLocation()
    {
        // --save-to writes a copy elsewhere; the connected source is untouched, so the
        // mutation must not be deployed to the workspace mirror.
        var begin = await MutationLifecycle.BeginAsync(
            [], new ModelReference("/local/model"),
            new MutationOptions(Save: false, SaveTo: "/elsewhere/copy", Stage: false, Revert: false, Serialization: "", Force: false),
            new Tomix.App.State.StagingStore(), WorkspaceConnection(), CancellationToken.None);

        Assert.Equal(MutationMode.Save, begin.Mode);
        Assert.Null(begin.Context!.SyncTarget);
    }

    private static Tomix.App.State.CliConnectionState WorkspaceConnection()
        => new(
            Server: null, Database: "MyModel", Model: "/local/model", Auth: null,
            Local: true, Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws");

    private static MutationContext NewSaveContext(ModelReference? syncTarget)
        => new(MutationMode.Save, new ModelReference("/local/model"), null, "tmdl", true, null, syncTarget);

    private sealed class StubMutationSession : IModelMutationSession, IModelDeploySession
    {
        private readonly bool _deploySucceeds;

        public StubMutationSession(bool deploySucceeds) => _deploySucceeds = deploySucceeds;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            if (_deploySucceeds)
                return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));

            throw new InvalidOperationException("Deploy failed for test purposes.");
        }

        public string GenerateScript(ModelDeployRequest request) => "";
    }

    private sealed class CancellingDeploySession : IModelDeploySession
    {
        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new OperationCanceledException();

        public string GenerateScript(ModelDeployRequest request) => "";
    }

    private sealed class StubNonDeployMutationSession : IModelMutationSession
    {
        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }
}
