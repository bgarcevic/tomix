using Tomix.App.Deps;
using Tomix.App.Find;
using Tomix.App.Get;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ReadOnlyCommandHandlerTests
{
    [Fact]
    public async Task Get_PrefersExactPathOverChildWithSameName()
    {
        var result = await new GetModelHandler([new StubModelProvider()]).HandleAsync(
            new GetModelRequest(new ModelReference("any"), "Sales", Query: null, Type: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Table", result.Data!.Type);
        Assert.Equal("Sales", result.Data.Path);
        Assert.Equal(1, result.Data.Properties["partitions"]);
    }

    [Fact]
    public async Task Find_SearchesPartitionsButOmitsRelationships()
    {
        var result = await new FindModelHandler([new StubModelProvider()]).HandleAsync(
            new FindModelRequest(
                new ModelReference("any"),
                "Sales",
                Scope: "all",
                Regex: false,
                CaseSensitive: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales", result.Data!.Pattern);
        Assert.Equal(
            [
                "Sales|Name|Sales|1|1",
                "Sales/Total Sales|Name|Sales|1|7",
                "Sales/Total Sales|Expression|Sales|1|5",
                "Sales/Order Count|Expression|Sales|1|11",
                "Sales/Sales|Name|Sales|1|1"
            ],
            result.Data.Matches.Select(m => $"{m.Path}|{m.Property}|{m.MatchedText}|{m.Line}|{m.Position}").ToArray());
    }

    [Theory]
    [InlineData("formatStrings", "#,0", "FormatString")]
    [InlineData("displayFolders", "KPIs", "DisplayFolder")]
    [InlineData("annotations", "isGeneralNumber", "Annotation:PBI_FormatHint")]
    public async Task Find_PropertyScopes_SearchSnapshotProperties(string scope, string pattern, string expectedField)
    {
        var result = await new FindModelHandler([new StubModelProvider()]).HandleAsync(
            new FindModelRequest(
                new ModelReference("any"),
                pattern,
                Scope: scope,
                Regex: false,
                CaseSensitive: false),
            CancellationToken.None);

        Assert.True(result.Success);
        var match = Assert.Single(result.Data!.Matches);
        Assert.Equal("Sales/Total Sales", match.Path);
        Assert.Equal(expectedField, match.Property);
    }

    [Fact]
    public async Task Find_AllScope_ExcludesAnnotations()
    {
        var result = await new FindModelHandler([new StubModelProvider()]).HandleAsync(
            new FindModelRequest(
                new ModelReference("any"),
                "isGeneralNumber",
                Scope: "all",
                Regex: false,
                CaseSensitive: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Matches);
    }

    [Fact]
    public async Task Find_InvalidRegexPattern_FailsWithDiagnostic()
    {
        var result = await new FindModelHandler([new StubModelProvider()]).HandleAsync(
            new FindModelRequest(
                new ModelReference("any"),
                "([unclosed",
                Scope: "all",
                Regex: true,
                CaseSensitive: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_FIND_INVALID_REGEX", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Deps_FindsColumnReferencesInMeasureExpression()
    {
        var result = await new DepsModelHandler([new StubModelProvider()]).HandleAsync(
            new DepsModelRequest(
                new ModelReference("any"),
                "Sales/Total Sales",
                Type: null,
                UpstreamOnly: false,
                DownstreamOnly: false,
                Deep: false,
                Unused: false,
                HiddenOnly: false,
                MaxDepth: 10),
            CancellationToken.None);

        Assert.True(result.Success);
        var dependency = Assert.Single(result.Data!.Upstream);
        Assert.Equal("Sales/Amount", dependency.Path);
        Assert.Equal("Column", dependency.Type);
        Assert.Empty(result.Data.Downstream);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession());
    }

    private sealed class StubSession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("(unnamed)", 1601, 1, 1, 2, 1, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
        {
            var amount = new ModelObject(
                "Amount", ModelObjectKind.Column, "Sales/Amount",
                Detail: "decimal", Expression: null, Description: null, Hidden: false,
                SourceColumn: "Amount", Children: []);
            var totalSales = new ModelObject(
                "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
                Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
                SourceColumn: null, Children: [],
                Properties: new Dictionary<string, string>
                {
                    ["FormatString"] = "#,0",
                    ["DisplayFolder"] = "KPIs",
                    ["Annotation:PBI_FormatHint"] = "{\"isGeneralNumber\":true}"
                });
            var orderCount = new ModelObject(
                "Order Count", ModelObjectKind.Measure, "Sales/Order Count",
                Detail: null, Expression: "COUNTROWS(Sales)", Description: null, Hidden: false,
                SourceColumn: null, Children: []);
            var partition = new ModelObject(
                "Sales", ModelObjectKind.Partition, "Sales/Sales",
                Detail: "import", Expression: null, Description: null, Hidden: false,
                SourceColumn: null, Children: []);
            var table = new ModelObject(
                "Sales", ModelObjectKind.Table, "Sales",
                Detail: "regular", Expression: null, Description: null, Hidden: false,
                SourceColumn: null, Children: [amount, totalSales, orderCount, partition]);
            var relationship = new ModelObject(
                "Customers[CustomerKey] -> Sales[CustomerKey]",
                ModelObjectKind.Relationship,
                "Relationships/rel-customers",
                Detail: null,
                Expression: null,
                Description: null,
                Hidden: false,
                SourceColumn: null,
                Children: []);

            return Task.FromResult(new ModelSnapshot("(unnamed)", 1601, [table, relationship]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
