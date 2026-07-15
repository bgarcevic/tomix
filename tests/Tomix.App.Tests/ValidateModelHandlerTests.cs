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

    [Fact]
    public async Task HandleAsync_IgnoresReferencesInStringsAndComments()
    {
        var expression = "\"Sales[Nope]\" & \"x\" // Sales[AlsoNope]\n& FORMAT(SUM(Sales[Amount]), \"0\")";
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_DetectsUnknownTable()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM('Missing Table'[X])")));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0001" && e.Message.Contains("Missing Table"));
    }

    [Fact]
    public async Task HandleAsync_ReportsLineOfBrokenReference()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM(Sales[Amount])\n+ SUM(Sales[Missing])")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("2", issue.Expression);
    }

    [Fact]
    public async Task HandleAsync_WarnsOnUnresolvedUnqualifiedReference()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "[No Such Measure] + 1")));

        Assert.True(result.Data!.Valid);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Data.Warnings, w => w.Code == "DAX0003" && w.Message.Contains("No Such Measure"));
    }

    [Fact]
    public async Task HandleAsync_SuppressesWarnings_WhenNoWarnings()
    {
        var result = await ValidateAsync(
            SalesSnapshot(Measure("Total", "[No Such Measure] + 1")),
            noWarnings: true);

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_SkipsVarNamesAndFunctions()
    {
        var expression = "VAR Segment = SUM(Sales[Amount]) RETURN Segment + COUNTROWS(Sales)";
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_SkipsMQueryPartitions()
    {
        var partition = new ModelObject("Part", ModelObjectKind.Partition, "Sales/Part",
            Detail: "m", Expression: "Table.SelectRows(Src, each Nope[Broken] > 0)", Description: null,
            Hidden: false, SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["PartitionSourceType"] = "M" });
        var result = await ValidateAsync(SalesSnapshot(partition));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_ScansSecondaryMeasureExpressions()
    {
        var measure = new ModelObject("Total", ModelObjectKind.Measure, "Sales/Total",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["DetailRowsExpression"] = "SELECTCOLUMNS(Sales, \"A\", Sales[Missing])" });
        var result = await ValidateAsync(SalesSnapshot(measure));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0002" && e.Message.Contains("Missing"));
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenRelationshipEndpoint()
    {
        var relationship = new ModelObject("Sales[Key] -> Product[Key]", ModelObjectKind.Relationship,
            "Relationships/Sales[Key] -> Product[Key]", Detail: null, Expression: null, Description: null,
            Hidden: false, SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string>
            {
                ["FromColumn"] = "Sales[Key]",
                ["ToColumn"] = "Product[Key]"
            });
        var result = await ValidateAsync(SalesSnapshot(relationship));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_RELATIONSHIP");
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenSortByColumn()
    {
        var column = new ModelObject("Month", ModelObjectKind.Column, "Sales/Month",
            Detail: "string", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Month", Children: [],
            Properties: new Dictionary<string, string> { ["SortByColumn"] = "MonthNo" });
        var result = await ValidateAsync(SalesSnapshot(column));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_SORT_BY" && e.Message.Contains("MonthNo"));
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenHierarchyLevel()
    {
        var level = new ModelObject("Year", ModelObjectKind.Level, "Sales/Calendar/Year",
            Detail: "YearCol", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var hierarchy = new ModelObject("Calendar", ModelObjectKind.Hierarchy, "Sales/Calendar",
            Detail: "1 levels", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [level]);
        var result = await ValidateAsync(SalesSnapshot(hierarchy));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_LEVEL" && e.Message.Contains("YearCol"));
    }

    [Fact]
    public async Task HandleAsync_ReportsLoadFailureAsValidationError()
    {
        var handler = new ValidateModelHandler([new ThrowingModelProvider()]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Valid);
        Assert.Equal(1, result.ExitCode);
        var issue = Assert.Single(result.Data.Errors);
        Assert.Equal("TOMIX_MODEL_LOAD_FAILED", issue.Code);
        Assert.Contains("cannot be resolved", issue.Message);
    }

    [Fact]
    public async Task HandleAsync_PreservesAuthRequiredDiagnostic()
    {
        var handler = new ValidateModelHandler(
            [new ThrowingModelProvider(new Tomix.Core.Authentication.AuthenticationRequiredException("Not authenticated. Run 'tx auth login'."))]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "Model"),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Contains("auth login", result.Diagnostics[0].Hint);
    }

    [Fact]
    public async Task HandleAsync_ReportsRemoteFailureAsConnectDiagnostic()
    {
        var handler = new ValidateModelHandler(
            [new ThrowingModelProvider(new InvalidOperationException("connection refused"))]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "Model"),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal("TOMIX_CONNECT_FAILED", result.Diagnostics[0].Code);
    }

    private sealed class ThrowingModelProvider : IModelProvider
    {
        private readonly Exception _exception;

        public ThrowingModelProvider(Exception? exception = null)
            => _exception = exception ?? new InvalidOperationException("Property FromColumn cannot be resolved.");

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => throw _exception;
    }

    private static Task<Tomix.Core.Results.TomixResult<ValidateModelResult>> ValidateAsync(
        ModelSnapshot snapshot, bool noWarnings = false)
        => new ValidateModelHandler([new StubModelProvider(snapshot)]).HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: noWarnings, ServerOnly: false),
            CancellationToken.None);

    private static ModelObject Measure(string name, string expression)
        => new(name, ModelObjectKind.Measure, $"Sales/{name}",
            Detail: null, Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);

    /// <summary>A Sales table with an Amount column, plus <paramref name="extras"/> as its children
    /// (relationships are hoisted to the snapshot root, as in real snapshots).</summary>
    private static ModelSnapshot SalesSnapshot(params ModelObject[] extras)
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var children = new List<ModelObject> { amount };
        var roots = new List<ModelObject>();
        foreach (var extra in extras)
        {
            if (extra.Kind == ModelObjectKind.Relationship)
                roots.Add(extra);
            else
                children.Add(extra);
        }

        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: children);

        return new ModelSnapshot("test", 1601, [sales, .. roots]);
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
