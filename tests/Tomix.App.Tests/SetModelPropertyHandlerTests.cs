using Tomix.App.Set;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

public sealed class SetModelPropertyHandlerTests
{

    private static Tomix.App.Mutations.MutationStores TestStores => new(
        new Tomix.App.State.StagingStore(
            Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"),
        () => null);
    [Fact]
    public async Task HandleAsync_Fails_WhenNoPropertyGiven()
    {
        var handler = new SetModelPropertyHandler([], TestStores);
        var result = await handler.HandleAsync(
            NewRequest(properties: [], revert: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_SET_PROPERTY_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_Fails_WhenRevertCombinedWithAssignment()
    {
        // A -q/-i next to --revert must hard-error instead of silently dropping the assignment.
        var handler = new SetModelPropertyHandler([], TestStores);
        var result = await handler.HandleAsync(
            NewRequest(properties: [new ModelPropertyAssignment("description", "x")], revert: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_CapturesOldExpressionValue_AndDaxFlag()
    {
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(Snapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("expression", "SUM(Sales[Amount]) + 1")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.IsDaxProperty);
        Assert.Equal("SUM(Sales[Amount])", result.Data.OldValue);
        Assert.Equal("SUM(Sales[Amount]) + 1", result.Data.Value);
    }

    [Fact]
    public async Task HandleAsync_MatchesDaxPropertyKeyCaseInsensitively()
    {
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(Snapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("EXPRESSION", "SUM(Sales[Amount]) + 1")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.IsDaxProperty);
        Assert.Equal("SUM(Sales[Amount])", result.Data.OldValue);
    }

    [Fact]
    public async Task HandleAsync_EmptyMeasureExpression_IsStillDax()
    {
        // The site list only yields non-empty expressions; a measure whose expression is being
        // written for the first time must still count as a DAX property.
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(EmptyMeasureSnapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("expression", "SUM(Sales[Amount])")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.IsDaxProperty);
        Assert.Null(result.Data.OldValue);
    }

    [Fact]
    public async Task HandleAsync_NonDaxProperty_HasNoPreviewData()
    {
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(Snapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("formatString", "\"$\"#,0")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.IsDaxProperty);
        Assert.Null(result.Data.OldValue);
    }

    [Fact]
    public async Task HandleAsync_UnresolvedTarget_HasNoPreviewData()
    {
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(Snapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("expression", "SUM(Sales[Amount])")],
                revert: false,
                path: "Sales/Missing"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.IsDaxProperty);
        Assert.Null(result.Data.OldValue);
    }

    [Fact]
    public async Task HandleAsync_ReportsPostMutationErrorCount()
    {
        // Setting the expression to reference a missing column: the result must carry the true
        // post-mutation error count (the measurement the save gate will gate on), not a 0.
        var handler = new SetModelPropertyHandler(
            [new StubProvider(new StubSession(Snapshot(), postMutation: BrokenSnapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("expression", "SUM(Sales[Missing])")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Data!.ValidationErrors);
    }

    [Fact]
    public async Task HandleAsync_ReportsZeroErrors_WhenPostMutationModelIsClean()
    {
        var handler = new SetModelPropertyHandler([new StubProvider(new StubSession(Snapshot()))], TestStores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new ModelPropertyAssignment("expression", "SUM(Sales[Amount]) + 1")],
                revert: false,
                path: "Sales/Total Sales"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Data!.ValidationErrors);
    }

    [Fact]
    public async Task HandleAsync_SeveralAssignments_ReachOneSetPropertyAndOneSave()
    {
        // Every --set rides in one SetProperty call, so the model is loaded and saved once.
        var session = new StubSession(Snapshot());
        var handler = new SetModelPropertyHandler([new StubProvider(session)], TestStores);
        IReadOnlyList<ModelPropertyAssignment> assignments =
        [
            new("formatString", "#,0"),
            new("displayFolder", "KPIs"),
            new("description", "Revenue")
        ];

        var result = await handler.HandleAsync(
            NewRequest(assignments, revert: false, path: "Sales/Total Sales", save: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(assignments, Assert.Single(session.SetRequests).Properties);
        Assert.Equal(1, session.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_SeveralAssignments_AllLandOnDisk()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();
        var handler = new SetModelPropertyHandler([new TmdlModelProvider()], config.Stores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new("formatString", "#,0"), new("displayFolder", "KPIs")],
                revert: false,
                path: "Sales/Total Sales",
                save: true,
                model: model.Path),
            CancellationToken.None);

        Assert.True(result.Success);
        var sales = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));
        Assert.Contains("formatString: #,0", sales);
        Assert.Contains("displayFolder: KPIs", sales);
    }

    [Fact]
    public async Task HandleAsync_LaterAssignmentFails_WritesNothing()
    {
        // Assignments apply in order, so the valid one has already changed the in-memory model
        // when the unknown property throws. The save must not run, or it would persist half the set.
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();
        var before = Files(model.Path);
        var handler = new SetModelPropertyHandler([new TmdlModelProvider()], config.Stores);

        var result = await handler.HandleAsync(
            NewRequest(
                [new("description", "Revenue"), new("noSuchProperty", "1")],
                revert: false,
                path: "Sales/Total Sales",
                save: true,
                model: model.Path),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, Files(model.Path));
    }

    private static SortedDictionary<string, string> Files(string root)
        => new(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllText), StringComparer.Ordinal);

    private static SetModelPropertyRequest NewRequest(
        IReadOnlyList<ModelPropertyAssignment> properties,
        bool revert,
        string path = "Sales",
        bool save = false,
        string model = "model.bim")
        => new(
            new ModelReference(model),
            path,
            properties,
            Type: null,
            Save: save,
            SaveTo: null,
            Serialization: "",
            Stage: false,
            Revert: revert,
            NoSync: false);

    private static ModelSnapshot Snapshot()
    {
        var totalSales = new ModelObject(
            "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var amount = new ModelObject(
            "Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var sales = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, totalSales]);

        return new ModelSnapshot("stub", 1601, [sales]);
    }

    private static ModelSnapshot EmptyMeasureSnapshot()
    {
        var totalSales = new ModelObject(
            "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var amount = new ModelObject(
            "Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var sales = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, totalSales]);

        return new ModelSnapshot("stub", 1601, [sales]);
    }

    /// <summary>The post-mutation model when the new expression references a missing column.</summary>
    private static ModelSnapshot BrokenSnapshot()
    {
        var totalSales = new ModelObject(
            "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Missing])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var amount = new ModelObject(
            "Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var sales = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, totalSales]);

        return new ModelSnapshot("stub", 1601, [sales]);
    }

    private sealed class StubProvider : IModelProvider
    {
        private readonly StubSession _session;

        public StubProvider(StubSession session) => _session = session;

        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class StubSession : IModelSession, IModelMutationSession
    {
        private readonly ModelSnapshot _snapshot;
        private readonly ModelSnapshot _postMutationSnapshot;
        private readonly List<ModelObjectSetRequest> _setRequests = [];
        private bool _mutated;

        public StubSession(ModelSnapshot snapshot, ModelSnapshot? postMutation = null)
        {
            _snapshot = snapshot;
            _postMutationSnapshot = postMutation ?? snapshot;
        }

        public IReadOnlyList<ModelObjectSetRequest> SetRequests => _setRequests;

        public int SaveCount { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(_mutated ? _postMutationSnapshot : _snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => throw new NotSupportedException();

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            _mutated = true;
            _setRequests.Add(request);
            return new(
                request.Path,
                Changed: true,
                Property: request.Properties[^1].Property,
                Value: request.Properties[^1].Value);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => throw new NotSupportedException();

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => throw new NotSupportedException();

        public Task<ModelExportResult> SaveAsync(
            string? outputPath,
            string serialization,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(new ModelExportResult(outputPath ?? "model.bim", serialization));
        }
    }
}
