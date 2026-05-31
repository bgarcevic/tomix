using Mdl.App.Ls;
using Mdl.Core.Models;

namespace Mdl.App.Tests;

public sealed class LsModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ListsTablesByDefault_WhenProviderCanOpen()
    {
        var handler = new LsModelHandler([new StubModelProvider()]);
        var result  = await handler.HandleAsync(
            new LsModelRequest(new ModelReference("any"), PathFilter: null, Type: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("stub", result.Data!.ModelName);
        var only = Assert.Single(result.Data.Objects);
        Assert.Equal("Sales", only.Name);
        Assert.Equal(ModelObjectKind.Table, only.Kind);
    }

    [Fact]
    public async Task HandleAsync_ProjectsDescriptionAndChildCounts()
    {
        var handler = new LsModelHandler([new StubModelProvider()]);
        var result  = await handler.HandleAsync(
            new LsModelRequest(new ModelReference("any"), PathFilter: null, Type: null),
            CancellationToken.None);

        var table = Assert.Single(result.Data!.Objects);
        Assert.Equal("Sales fact table", table.Description);
        Assert.Equal(1, table.ChildCounts.GetValueOrDefault(ModelObjectKind.Measure));
        Assert.Equal(0, table.ChildCounts.GetValueOrDefault(ModelObjectKind.Column));
    }

    [Fact]
    public async Task HandleAsync_AppliesPathFilter()
    {
        var handler = new LsModelHandler([new StubModelProvider()]);
        var result  = await handler.HandleAsync(
            new LsModelRequest(new ModelReference("any"), PathFilter: "Measures", Type: null),
            CancellationToken.None);

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Objects);
        Assert.Equal("Total Sales", only.Name);
        Assert.Equal(ModelObjectKind.Measure, only.Kind);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var handler = new LsModelHandler([]);
        var result  = await handler.HandleAsync(
            new LsModelRequest(new ModelReference("any"), PathFilter: null, Type: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("MDL_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession());
    }

    private sealed class StubSession : IModelSession
    {
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 3, 12, 4, 2, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
        {
            var measure = new ModelObject(
                "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
                Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false, Children: []);
            var sales = new ModelObject(
                "Sales", ModelObjectKind.Table, "Sales",
                Detail: "regular", Expression: null, Description: "Sales fact table", Hidden: false,
                Children: [measure]);

            return Task.FromResult(new ModelSnapshot("stub", 1601, [sales]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
