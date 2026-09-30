using Tomix.App.Get;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Tests;

public sealed class GetListTests
{
    [Fact]
    public async Task HandleAsync_ListsTablesByDefault_WhenProviderCanOpen()
    {
        var result = await List([new StubModelProvider()], null, null);

        Assert.True(result.Success);
        Assert.Equal("stub", result.Data!.ModelName);
        var only = Assert.Single(result.Data.Objects);
        Assert.Equal("Sales", only.Name);
        Assert.Equal(ModelObjectKind.Table, only.Kind);
    }

    [Fact]
    public async Task HandleAsync_ProjectsDescriptionAndChildCounts()
    {
        var result = await List([new StubModelProvider()], null, null);

        var table = Assert.Single(result.Data!.Objects);
        Assert.Equal("Sales fact table", table.Description);
        Assert.Equal(1, table.ChildCounts.GetValueOrDefault(ModelObjectKind.Measure));
        Assert.Equal(0, table.ChildCounts.GetValueOrDefault(ModelObjectKind.Column));
    }

    [Fact]
    public async Task HandleAsync_AppliesPathFilter()
    {
        var result = await List([new StubModelProvider()], "Measures", null);

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Objects);
        Assert.Equal("Total Sales", only.Name);
        Assert.Equal(ModelObjectKind.Measure, only.Kind);
    }

    [Fact]
    public async Task HandleAsync_ChildCounts_CarryCalculatedColumnsSeparately()
    {
        var result = await List([new StubModelProvider()], null, null);

        var table = Assert.Single(result.Data!.Objects);
        Assert.Equal(1, table.ChildCounts.GetValueOrDefault(ModelObjectKind.CalculatedColumn));
        Assert.Equal(0, table.ChildCounts.GetValueOrDefault(ModelObjectKind.Column));
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var result = await List([], null, null);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("Name=Total Sales", 1)]     // exact, whole value
    [InlineData("name=TOTAL SALES", 1)]     // case-insensitive on both sides
    [InlineData("Name=Total", 0)]           // no '*' never means contains
    [InlineData("Name=Total*", 1)]          // starts-with
    [InlineData("Name=*Sales", 1)]          // ends-with
    [InlineData("Name=*tal Sa*", 1)]        // contains
    [InlineData("Name=*nope*", 0)]
    [InlineData("IsHidden=false", 1)]       // booleans compare as true/false
    public async Task Where_MatchesTheFourPatternForms(string where, int expected)
    {
        var result = await List([new StubModelProvider()], "Measures", null, where);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Data!.Objects.Count);
    }

    [Fact]
    public async Task Where_RepeatedFiltersAnd()
    {
        var both = await List([new StubModelProvider()], "Sales", null, "Name=*a*", "Expression=*Cost*");

        Assert.Equal("Margin", Assert.Single(both.Data!.Objects).Name);
    }

    [Fact]
    public async Task Where_WithoutPath_ScopesToTables()
    {
        var result = await List([new StubModelProvider()], null, null, "Name=*a*");

        Assert.Equal(ModelObjectKind.Table, Assert.Single(result.Data!.Objects).Kind);
    }

    [Fact]
    public async Task Where_UnknownProperty_FailsWithSuggestion()
    {
        var result = await List([new StubModelProvider()], "Measures", null, "Nmae=Total*");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_PROPERTY_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Contains("'name'", result.Diagnostics[0].Hint);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("=Value")]
    [InlineData("")]
    public void PropertyFilter_RejectsTextWithoutPropAndEquals(string text)
        => Assert.False(PropertyFilter.TryParse(text, out _));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Tables", true)]
    [InlineData("Sales/Measures", true)]
    [InlineData("Sa*", true)]
    [InlineData("*/Amount", true)]
    [InlineData("Sales", false)]
    [InlineData("Sales/Amount", false)]
    [InlineData("Sales/Measures/Total Sales", false)]
    [InlineData("'Sales'[Amount]", false)]
    [InlineData("[Total Sales]", false)]
    [InlineData(".", false)]
    [InlineData("Sales/'*literal*'", false)]
    public void IsSelection_TellsSetsFromSingleObjects(string? path, bool expected)
        => Assert.Equal(expected, GetModelHandler.IsSelection(path));

    [Theory]
    [InlineData("Sa*", "--query")]
    [InlineData("Measures", "--query")]
    public async Task Query_OnASelection_IsRejected(string path, string option)
    {
        var result = await new GetModelHandler([new StubModelProvider()]).HandleAsync(
            new GetModelRequest(new ModelReference("any"), path, Query: "name", Type: null),
            CancellationToken.None);

        Assert.Equal("TOMIX_SINGLE_OBJECT_REQUIRED", result.Diagnostics[0].Code);
        Assert.Contains(option, result.Diagnostics[0].Message);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("Sa*")]
    [InlineData("Sales/Measures")]
    public async Task Deps_OnASelection_IsRejected(string path)
    {
        var result = await new GetModelHandler([new StubModelProvider()]).HandleAsync(
            new GetModelRequest(new ModelReference("any"), path, Query: null, Type: null, Mode: GetMode.Deps),
            CancellationToken.None);

        Assert.Equal("TOMIX_SINGLE_OBJECT_REQUIRED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Unused_WithAPath_IsRejected()
    {
        var result = await new GetModelHandler([new StubModelProvider()]).HandleAsync(
            new GetModelRequest(new ModelReference("any"), "Sales", Query: null, Type: null, Mode: GetMode.Unused),
            CancellationToken.None);

        Assert.Equal("TOMIX_UNUSED_PATH", result.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("Sa*")]
    [InlineData("Tables")]
    [InlineData(null)]
    public async Task Auto_ListsASelection_AndReadsASingleObject(string? selection)
    {
        var handler = new GetModelHandler([new StubModelProvider()]);

        var listed = await handler.HandleAsync(
            new GetModelRequest(new ModelReference("any"), selection, Query: null, Type: null), CancellationToken.None);
        var read = await handler.HandleAsync(
            new GetModelRequest(new ModelReference("any"), "Sales", Query: null, Type: null), CancellationToken.None);

        Assert.Equal(GetMode.List, listed.Data!.Mode);
        Assert.Equal("Sales", Assert.Single(listed.Data.List!.Objects).Path);
        Assert.Equal(GetMode.Object, read.Data!.Mode);
        Assert.Equal("Sales", read.Data.Object!.Path);
    }

    private static async Task<TomixResult<GetListResult>> List(
        IEnumerable<IModelProvider> providers,
        string? pathFilter,
        ModelObjectKind? type,
        params string[] where)
    {
        var filters = where.Select(text => PropertyFilter.TryParse(text, out var filter) ? filter : throw new ArgumentException(text)).ToList();
        var result = await new GetModelHandler(providers).HandleAsync(
            new GetModelRequest(new ModelReference("any"), pathFilter, Query: null, type, GetMode.List, filters),
            CancellationToken.None);
        return new(result.Success, result.Data?.List, result.Diagnostics, result.ExitCode);
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
            => Task.FromResult(new ModelSummary("stub", 1601, 3, 12, 4, 2, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
        {
            var measure = new ModelObject(
                "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
                Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
                SourceColumn: null, Children: []);
            var margin = new ModelObject(
                "Margin", ModelObjectKind.CalculatedColumn, "Sales/Margin",
                Detail: null, Expression: "SUM(Sales[Amount]) - SUM(Sales[Cost])", Description: null,
                Hidden: false, SourceColumn: null, Children: []);
            var sales = new ModelObject(
                "Sales", ModelObjectKind.Table, "Sales",
                Detail: "regular", Expression: null, Description: "Sales fact table", Hidden: false,
                SourceColumn: null, Children: [measure, margin]);

            return Task.FromResult(new ModelSnapshot("stub", 1601, [sales]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
