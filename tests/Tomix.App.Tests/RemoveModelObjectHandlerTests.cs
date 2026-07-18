using Tomix.App.Rm;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class RemoveModelObjectHandlerTests
{

    private static Tomix.App.Mutations.MutationStores TestStores => new(
        new Tomix.App.State.StagingStore(
            Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"),
        () => null);
    [Fact]
    public async Task HandleAsync_IfExistsOnMissingObject_ReportsNotRemovedWithReason()
    {
        var handler = new RemoveModelObjectHandler([new StubProvider(new StubSession(removeChanged: false))], TestStores);

        var result = await handler.HandleAsync(Request("Sales/Nope", ifExists: true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(false, result.Data!.Removed);
        Assert.Equal("not_found", result.Data.Reason);
        Assert.Equal("Sales/Nope", result.Data.Path);
        Assert.False(result.Data.Reverted);
    }

    [Fact]
    public async Task HandleAsync_Removed_ReportsPath()
    {
        var handler = new RemoveModelObjectHandler([new StubProvider(new StubSession(removeChanged: true))], TestStores);

        var result = await handler.HandleAsync(Request("Sales/Old", ifExists: false), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales/Old", result.Data!.Removed);
        Assert.Null(result.Data.Reason);
    }

    [Fact]
    public async Task HandleAsync_RevertWithNothingStaged_Fails()
    {
        var handler = new RemoveModelObjectHandler([new StubProvider(new StubSession(removeChanged: true))], TestStores);

        var result = await handler.HandleAsync(
            Request("Sales/Old", ifExists: false) with
            {
                Model = new ModelReference($"/nonexistent/{Guid.NewGuid():N}.bim"),
                Revert = true
            },
            CancellationToken.None);

        // Revert used to report success unconditionally — even with nothing staged (live QA finding).
        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_NOTHING_STAGED", result.Diagnostics[0].Code);
    }

    private static RemoveModelObjectRequest Request(string path, bool ifExists)
        => new(
            new ModelReference("any"),
            path,
            Type: null,
            ifExists,
            DryRun: false,
            Save: false,
            SaveTo: null,
            Serialization: "",
            Force: false);

    private sealed class StubProvider : IModelProvider
    {
        private readonly IModelSession _session;

        public StubProvider(IModelSession session) => _session = session;

        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult(_session);
    }

    private sealed class StubSession : IModelSession, IModelMutationSession
    {
        private readonly bool _removeChanged;

        public StubSession(bool removeChanged) => _removeChanged = removeChanged;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => throw new NotSupportedException();
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: _removeChanged, Reason: _removeChanged ? null : "not_found");
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "source", serialization));
    }
}
