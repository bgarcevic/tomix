using Tomix.App.Set;
using Tomix.Core.Models;

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

    private static SetModelPropertyRequest NewRequest(
        IReadOnlyList<ModelPropertyAssignment> properties,
        bool revert,
        string path = "Sales")
        => new(
            new ModelReference("model.bim"),
            path,
            properties,
            Type: null,
            Save: false,
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

        public StubSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(_snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => throw new NotSupportedException();

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => new(
                request.Path,
                Changed: true,
                Property: request.Properties[^1].Property,
                Value: request.Properties[^1].Value);

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => throw new NotSupportedException();

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => throw new NotSupportedException();

        public Task<ModelExportResult> SaveAsync(
            string? outputPath,
            string serialization,
            bool overwrite,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
