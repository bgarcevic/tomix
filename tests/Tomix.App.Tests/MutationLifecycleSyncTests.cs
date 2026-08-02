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

    /// <summary>
    /// Drift guard for the one deploy in the product that is deliberately NOT preserve-by-default.
    /// The mutation the user just made may be to a partition, a data source, or a role member, and
    /// preserving those on the mirror would silently discard the edit they asked for. Granular
    /// deployment made <c>Preserve</c> the default everywhere else, so this line is one careless
    /// "make it consistent" away from breaking, and nothing else would notice.
    ///
    /// Note the cost this accepts: a mirror carrying incremental-refresh partitions loses their
    /// processed data on every synced mutation. See issue #129.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_SyncsAsFullDeploy_NotPreserving()
    {
        var session = new StubMutationSession(deploySucceeds: true);

        await MutationLifecycle.CompleteAsync(
            session, NewSaveContext(SyncTarget), "set", "set X", CancellationToken.None);

        Assert.Equal(ModelDeployOptions.Full, session.LastDeployRequest?.EffectiveOptions);
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
            new Tomix.App.State.StagingStore(Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"), WorkspaceConnection(), CancellationToken.None);

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
            new Tomix.App.State.StagingStore(Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"), WorkspaceConnection(), CancellationToken.None);

        Assert.Equal(MutationMode.Save, begin.Mode);
        Assert.Null(begin.Context!.SyncTarget);
    }

    [Fact]
    public async Task BeginAsync_SkipsSyncTarget_WhenSourceIsNotTheSessionPrimary()
    {
        // A save addressed with an explicit source (-s/-d or a model path) is not an edit to
        // the session's primary model; deploying it to the session's mirror would replace the
        // mirrored model with an unrelated one (#134).
        var begin = await MutationLifecycle.BeginAsync(
            [], new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/sandbox", "model-a"),
            new MutationOptions(Save: true, SaveTo: null, Stage: false, Revert: false, Serialization: "", Force: false),
            new Tomix.App.State.StagingStore(Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"), WorkspaceConnection(), CancellationToken.None);

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

        public ModelDeployRequest? LastDeployRequest { get; private set; }

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
            LastDeployRequest = request;
            if (_deploySucceeds)
                return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));

            throw new InvalidOperationException("Deploy failed for test purposes.");
        }

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class CancellingDeploySession : IModelDeploySession
    {
        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new OperationCanceledException();

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");
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
