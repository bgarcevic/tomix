using Tomix.App.Validate;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ValidateModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsValid_WhenNoErrors()
    {
        var handler = new ValidateModelHandler([new StubModelProvider(ValidSnapshot())]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenColumnReference()
    {
        var snapshot = SnapshotWithBrokenMeasure();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Valid);
        Assert.NotEmpty(result.Data.Errors);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0002" && e.Message.Contains("Missing"));
    }

    [Fact]
    public async Task HandleAsync_SkipsLocalAnalysis_WhenServerOnly()
    {
        var snapshot = SnapshotWithBrokenMeasure();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var handler = new ValidateModelHandler([]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_DeduplicatesIdenticalErrors()
    {
        var expression = "SUM(Sales[Missing]) + SUM(Sales[Missing])";
        var measure = new ModelObject("Broken", ModelObjectKind.Measure, "Sales/Broken",
            Detail: null, Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "int64", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        var snapshot = new ModelSnapshot("test", 1601, [sales]);
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.Single(result.Data!.Errors);
    }

    [Fact]
    public async Task HandleAsync_SkipsMeasureReferences()
    {
        var snapshot = SnapshotWithMeasureReference();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
    }

    private static ModelSnapshot SnapshotWithMeasureReference()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var otherMeasure = new ModelObject("Total Revenue", ModelObjectKind.Measure, "Sales/Total Revenue",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "Sales[Total Revenue]", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, otherMeasure, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private static ModelSnapshot ValidSnapshot()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private static ModelSnapshot SnapshotWithBrokenMeasure()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Missing])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        private readonly ModelSnapshot _snapshot;

        public StubModelProvider(ModelSnapshot snapshot) => _snapshot = snapshot;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession(_snapshot));
    }

    private sealed class StubSession : IModelSession
    {
        private readonly ModelSnapshot _snapshot;

        public StubSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(_snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
