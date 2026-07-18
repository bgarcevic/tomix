using Tomix.App.Rm;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Removing an object that DAX still references cannot be fixed up like a rename — the target
/// is gone. rm blocks the removal listing the referencing objects; --force removes anyway and
/// reports them as broken. References from inside a removed table never block: they vanish
/// with it.
/// </summary>
public sealed class RemoveReferenceGuardTests
{

    private static Tomix.App.Mutations.MutationStores TestStores => new(
        new Tomix.App.State.StagingStore(
            Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"),
        () => null);
    [Fact]
    public async Task RemoveReferencedMeasure_Fails_WithoutMutating()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", force: false);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.Contains("Sales/Derived", result.Diagnostics[0].Message);
        Assert.False(session.RemoveCalled);
    }

    [Fact]
    public async Task RemoveReferencedMeasure_Force_RemovesAndReportsBrokenReferences()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", force: true);

        Assert.True(result.Success);
        Assert.True(session.RemoveCalled);
        Assert.Equal(["Sales/Derived"], result.Data!.BrokenReferences);
    }

    [Fact]
    public async Task RemoveUnreferencedMeasure_Succeeds_WithoutForce()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Lonely", force: false);

        Assert.True(result.Success);
        Assert.True(session.RemoveCalled);
        Assert.Null(result.Data!.BrokenReferences);
    }

    [Fact]
    public async Task RemoveTable_ReferenceToItsColumn_Blocks()
    {
        // 'Sales'[Amount] disappears with the Sales table, so Region/Outside breaks even
        // though nothing references the table itself.
        var session = NewSession();
        var result = await Handle(session, "Sales", force: false);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.Contains("Region/Outside", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task RemoveTable_ReferencesInsideTheTable_DoNotBlock()
    {
        // Sales/Derived references Sales/Base, but both are removed with the table — only the
        // outside reference counts.
        var session = NewSession();
        var result = await Handle(session, "Sales", force: false);

        Assert.DoesNotContain("Sales/Derived", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task RemoveDaxFormPath_IsGuardedToo()
    {
        var session = NewSession();
        var result = await Handle(session, "'Sales'[Base]", force: false);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task CascadeRemoved_FlowsThroughToTheResult()
    {
        var session = NewSession(cascadeRemoved: ["relationship 'Sales'[Key] -> 'Region'[Key]"]);
        var result = await Handle(session, "Sales/Lonely", force: false);

        Assert.True(result.Success);
        Assert.Equal(["relationship 'Sales'[Key] -> 'Region'[Key]"], result.Data!.CascadeRemoved);
    }

    private static Task<Core.Results.TomixResult<RemoveModelObjectResult>> Handle(
        StubSnapshotSession session, string path, bool force)
        => new RemoveModelObjectHandler([new StubProvider(session)], TestStores).HandleAsync(
            new RemoveModelObjectRequest(
                new ModelReference("model.bim"),
                path, Type: null,
                IfExists: false, DryRun: false,
                Save: false, SaveTo: null, Serialization: "", Force: force),
            CancellationToken.None);

    /// <summary>
    /// Sales: measures Base ("1"), Derived ("[Base] * 2"), Lonely ("2"), column Amount.
    /// Region: measure Outside ("SUM('Sales'[Amount])").
    /// </summary>
    private static StubSnapshotSession NewSession(IReadOnlyList<string>? cascadeRemoved = null)
        => new(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Base", ModelObjectKind.Measure, "Sales/Base",
                Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Derived", ModelObjectKind.Measure, "Sales/Derived",
                Detail: null, Expression: "[Base] * 2", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Lonely", ModelObjectKind.Measure, "Sales/Lonely",
                Detail: null, Expression: "2", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Region", ModelObjectKind.Table, "Region",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Outside", ModelObjectKind.Measure, "Region/Outside",
                Detail: null, Expression: "SUM('Sales'[Amount])", Description: null, Hidden: false, SourceColumn: null, Children: [])
        ]), cascadeRemoved);

    private sealed class StubProvider : IModelProvider
    {
        private readonly StubSnapshotSession _session;

        public StubProvider(StubSnapshotSession session) => _session = session;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class StubSnapshotSession : IModelSession, IModelMutationSession
    {
        private readonly ModelSnapshot _snapshot;
        private readonly IReadOnlyList<string>? _cascadeRemoved;

        public StubSnapshotSession(ModelSnapshot snapshot, IReadOnlyList<string>? cascadeRemoved)
        {
            _snapshot = snapshot;
            _cascadeRemoved = cascadeRemoved;
        }

        public bool RemoveCalled { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 2, 4, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(_snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
        {
            RemoveCalled = true;
            return new ModelObjectMutationResult(request.Path, Changed: true, CascadeRemoved: _cascadeRemoved);
        }

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }
}
