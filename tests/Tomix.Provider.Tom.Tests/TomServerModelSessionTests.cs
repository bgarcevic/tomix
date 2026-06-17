using Tomix.Core.Models;
using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom.Tests;

public sealed class TomServerModelSessionTests
{
    [Fact]
    public async Task Session_ImplementsIModelMutationSession()
    {
        using var server = new Server();
        var database = NewDatabase();
        await using var session = new TomServerModelSession(server, database, null);

        Assert.IsAssignableFrom<IModelMutationSession>(session);
    }

    [Fact]
    public async Task AddObject_DelegatesToMutator()
    {
        using var server = new Server();
        var database = WithSales();
        await using var session = new TomServerModelSession(server, database, null);
        var mutator = (IModelMutationSession)session;

        var result = mutator.AddObject(new ModelObjectAddRequest(
            "Sales/Revenue", "Measure", "SUM(Sales[Amount])", [], IfNotExists: false));

        Assert.True(result.Changed);
        Assert.Equal("Sales/Revenue", result.Path);
        Assert.NotNull(database.Model.Tables["Sales"].Measures.Find("Revenue"));
    }

    [Fact]
    public async Task SetProperty_DelegatesToMutator()
    {
        using var server = new Server();
        var database = WithSalesMeasure();
        await using var session = new TomServerModelSession(server, database, null);
        var mutator = (IModelMutationSession)session;

        var result = mutator.SetProperty(new ModelObjectSetRequest(
            "Sales/Revenue", [new ModelPropertyAssignment("description", "Total revenue")], null));

        Assert.True(result.Changed);
        Assert.Equal("Total revenue", database.Model.Tables["Sales"].Measures.Find("Revenue").Description);
    }

    [Fact]
    public async Task RemoveObject_DelegatesToMutator()
    {
        using var server = new Server();
        var database = WithSalesMeasure();
        await using var session = new TomServerModelSession(server, database, null);
        var mutator = (IModelMutationSession)session;

        var result = mutator.RemoveObject(new ModelObjectRemoveRequest("Sales/Revenue", null, IfExists: false));

        Assert.True(result.Changed);
        Assert.Null(database.Model.Tables["Sales"].Measures.Find("Revenue"));
    }

    [Fact]
    public async Task ReplaceText_DelegatesToMutator()
    {
        using var server = new Server();
        var database = WithSalesMeasure();
        await using var session = new TomServerModelSession(server, database, null);
        var mutator = (IModelMutationSession)session;

        var result = mutator.ReplaceText(new ModelReplaceRequest(
            "SUM", "TOTAL", "expressions", Regex: false, CaseSensitive: false, Apply: true));

        Assert.True(result.ChangeCount > 0);
    }

    private static Database NewDatabase()
        => new() { Name = "Test", Model = new Model { Name = "Model" } };

    private static Database WithSales()
    {
        var db = NewDatabase();
        var sales = new Table { Name = "Sales" };
        sales.Partitions.Add(new Partition
        {
            Name = "Sales",
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        db.Model.Tables.Add(sales);
        return db;
    }

    private static Database WithSalesMeasure()
    {
        var db = WithSales();
        db.Model.Tables["Sales"].Measures.Add(new Measure { Name = "Revenue", Expression = "SUM(Sales[Amount])" });
        return db;
    }
}
