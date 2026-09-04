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
    /// Drift guard for the one deploy in the product that is deliberately NOT preserve-by-default,
    /// now pinned in both directions (issue #129, fixed).
    ///
    /// Outbound: the mutation the user just made may be to a partition, a data source, or a role
    /// member, and preserving those on the mirror would silently discard the edit they asked for.
    /// Granular deployment made <c>Preserve</c> the default everywhere else, so this is one careless
    /// "make it consistent" away from breaking, and nothing else would notice.
    ///
    /// Inbound: incremental-refresh policy partitions are the exemption — they are generated and
    /// processed on the service, so deploying them would discard processed data on every synced
    /// mutation, even a measure rename. The <c>incremental-refresh</c> command itself opts back into
    /// <see cref="ModelDeployOptions.Full"/>, because the preserve path clones the target's
    /// refreshPolicy back and would otherwise revert the policy edit.
    /// </summary>
    [Theory]
    [InlineData("set", false)]
    [InlineData("add", false)]
    [InlineData("rm", false)]
    [InlineData("incremental-refresh", true)]
    public async Task CompleteAsync_SyncOptions_PreservePolicyPartitionsExceptForRefreshPolicyCommand(
        string command, bool deploysPolicyPartitions)
    {
        var session = new StubMutationSession(deploySucceeds: true);

        await MutationLifecycle.CompleteAsync(
            session, NewSaveContext(SyncTarget), command, $"{command} X", CancellationToken.None);

        var expected = deploysPolicyPartitions
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };
        Assert.Equal(expected, session.LastDeployRequest?.EffectiveOptions);
    }

    [Theory]
    [InlineData("set", false)]
    [InlineData("add", false)]
    [InlineData("rm", false)]
    [InlineData("replace", false)]
    [InlineData("incremental-refresh", true)]
    public void SyncOptionsFor_Command_DeploysPolicyPartitions_OnlyForRefreshPolicyCommand(
        string command, bool deploysPolicyPartitions)
    {
        var options = WorkspaceSync.SyncOptionsFor(command);

        // Record equality also pins that every other aspect stays a full overwrite.
        var expected = deploysPolicyPartitions
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };
        Assert.Equal(expected, options);
    }

    [Theory]
    [InlineData(new[] { "add" }, false)]
    [InlineData(new[] { "add", "set" }, false)]
    [InlineData(new[] { "incremental-refresh" }, true)]
    [InlineData(new[] { "add", "incremental-refresh", "set" }, true)]
    public void SyncOptionsFor_Commands_DeploysPolicyPartitions_WhenAnyOpEditsTheRefreshPolicy(
        string[] commands, bool deploysPolicyPartitions)
    {
        var options = WorkspaceSync.SyncOptionsFor(commands);

        var expected = deploysPolicyPartitions
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };
        Assert.Equal(expected, options);
    }

    [Fact]
    public void SyncOptionsFor_EmptyCommands_PreservesPolicyPartitions()
        => Assert.False(WorkspaceSync.SyncOptionsFor(Array.Empty<string>()).DeployPolicyPartitions);

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
            SyncTarget, force: false, ModelDeployOptions.Full, CancellationToken.None);

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
                new CancellingDeploySession(), SyncTarget, force: false,
                ModelDeployOptions.Full, CancellationToken.None));
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

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This stub never plans a deploy.");
    }

    private sealed class CancellingDeploySession : IModelDeploySession
    {
        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new OperationCanceledException();

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This stub never plans a deploy.");
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
