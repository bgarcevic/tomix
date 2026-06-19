using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

public sealed class TomModelRefresherTests
{
    [Fact]
    public void GenerateRefreshScript_FullModel_UsesSingleDatabaseObject()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: null, Partitions: null);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"refresh\":{", script);
        Assert.Contains("\"type\":\"full\"", script);
        // Full model refresh uses a single {"database":"<name>"} object, not per-table enumeration.
        Assert.Contains("\"objects\":[{\"database\":\"TestModel\"}]", script);
        Assert.DoesNotContain("\"table\"", script);
        // applyRefreshPolicy is omitted when default (true); only emitted when explicitly false.
        Assert.DoesNotContain("\"applyRefreshPolicy\"", script);
    }

    [Fact]
    public void GenerateRefreshScript_TableScope_ListsTableObjects()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: ["Sales", "Customers"], Partitions: null);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"objects\":[{\"database\":\"TestModel\",\"table\":\"Sales\"},{\"database\":\"TestModel\",\"table\":\"Customers\"}]", script);
    }

    [Fact]
    public void GenerateRefreshScript_PartitionScope_ListsTablePartitionObjects()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: null, Partitions: [new TablePartition("Sales", "Internet"), new TablePartition("Sales", "Reseller")]);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("{\"database\":\"TestModel\",\"table\":\"Sales\",\"partition\":\"Internet\"}", script);
        Assert.Contains("{\"database\":\"TestModel\",\"table\":\"Sales\",\"partition\":\"Reseller\"}", script);
        Assert.DoesNotContain("\"applyRefreshPolicy\":false", script);
    }

    [Fact]
    public void GenerateRefreshScript_EscapesTableNamesWithQuotes()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: ["My\"Weird\"Table"], Partitions: null);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("{\"database\":\"TestModel\",\"table\":\"My\\\"Weird\\\"Table\"}", script);
    }

    [Theory]
    [InlineData("full", "full")]
    [InlineData("automatic", "automatic")]
    [InlineData("auto", "automatic")]
    [InlineData("dataonly", "dataOnly")]
    [InlineData("dataOnly", "dataOnly")]
    [InlineData("calculate", "calculate")]
    [InlineData("clearvalues", "clearValues")]
    [InlineData("defragment", "defragment")]
    [InlineData("add", "add")]
    public void NormalizeRefreshType_MapsTeAliases(string input, string expected)
        => Assert.Equal(expected, TomModelRefresher.NormalizeRefreshType(input));

    [Fact]
    public void NormalizeRefreshType_ThrowsOnUnknown()
    {
        Assert.Throws<InvalidOperationException>(() => TomModelRefresher.NormalizeRefreshType("bogus"));
    }

    [Fact]
    public void GenerateRefreshScript_EmptyTypeDefaultsToAutomatic()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(db.Name, "", null, null);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"type\":\"automatic\"", script);
    }

    [Fact]
    public void GenerateRefreshScript_IncludesEffectiveDate_WhenProvided()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: null, Partitions: null,
            ApplyRefreshPolicy: true,
            EffectiveDate: new DateOnly(2026, 1, 15));

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"effectiveDate\":\"2026-01-15\"", script);
    }

    [Fact]
    public void GenerateRefreshScript_IncludesMaxParallelism_WhenProvided()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: null, Partitions: null,
            ApplyRefreshPolicy: true, EffectiveDate: null,
            MaxParallelism: 4);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"maxParallelism\":4", script);
    }

    [Fact]
    public void GenerateRefreshScript_AppliesApplyRefreshPolicyFalse()
    {
        var db = NewDatabase();
        var request = new ModelRefreshRequest(
            Database: db.Name, RefreshType: "full",
            Tables: null, Partitions: null,
            ApplyRefreshPolicy: false);

        var script = TomModelRefresher.GenerateRefreshScript(db, request);

        Assert.Contains("\"applyRefreshPolicy\":false", script);
    }

    [Fact]
    public async Task Session_ImplementsIModelRefreshSession()
    {
        using var server = new Server();
        var db = NewDatabase();
        await using var session = new TomServerModelSession(server, db, null);

        Assert.IsAssignableFrom<IModelRefreshSession>(session);
    }

    [Fact]
    public async Task GenerateRefreshScriptViaSession_EmitsValidTmsl()
    {
        using var server = new Server();
        var db = NewDatabase();
        await using var session = new TomServerModelSession(server, db, null);
        var refresher = (IModelRefreshSession)session;

        var script = refresher.GenerateRefreshScript(
            new ModelRefreshRequest(db.Name, "full", ["Sales"], null));

        Assert.Contains("\"refresh\":{", script);
        Assert.Contains("\"table\":\"Sales\"", script);
    }

    private static Database NewDatabase()
    {
        var db = new Database { Name = "TestModel" };
        db.Model = new Model { Name = "Model" };
        db.Model.Tables.Add(new Table { Name = "Sales" });
        db.Model.Tables.Add(new Table { Name = "Customers" });
        return db;
    }
}
