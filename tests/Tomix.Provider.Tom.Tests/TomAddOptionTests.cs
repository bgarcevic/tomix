using Tomix.Core.Models;
using Tomix.Provider.Tom;
using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Each previously-dead <c>tx add</c> option now feeds the provider. These tests assert the
/// observable effect of one option each, end-to-end into the created TOM object.
/// </summary>
public sealed class TomAddOptionTests
{
    [Fact]
    public void Columns_CreatesColumnsOnNewTable()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Products", "Table") with { Columns = "Sku, Price" });

        var table = db.Model.Tables.Single(t => t.Name == "Products");
        Assert.Contains(table.Columns, c => c.Name == "Sku" && c is DataColumn);
        Assert.Contains(table.Columns, c => c.Name == "Price" && c is DataColumn);
    }

    [Fact]
    public void Mode_SetsPartitionStorageMode()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/P", "MPartition") with { Mode = "DirectQuery" });

        var partition = Sales(db).Partitions.Single(p => p.Name == "P");
        Assert.Equal(ModeType.DirectQuery, partition.Mode);
    }

    [Fact]
    public void PartitionExpression_SetsMPartitionSourceExpression()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/P", "MPartition") with { PartitionExpression = "let X = 1 in X" });

        var source = Assert.IsType<MPartitionSource>(Sales(db).Partitions.Single(p => p.Name == "P").Source);
        Assert.Equal("let X = 1 in X", source.Expression);
    }

    [Fact]
    public void SourceTable_SetsEntityPartitionEntityName()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/EP", "EntityPartition") with { SourceTable = "Orders" });

        var source = Assert.IsType<EntityPartitionSource>(Sales(db).Partitions.Single(p => p.Name == "EP").Source);
        Assert.Equal("Orders", source.EntityName);
    }

    [Fact]
    public void SourceSchema_SetsEntityPartitionSchemaName()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/EP", "EntityPartition") with { SourceTable = "Orders", SourceSchema = "dbo" });

        var source = Assert.IsType<EntityPartitionSource>(Sales(db).Partitions.Single(p => p.Name == "EP").Source);
        Assert.Equal("dbo", source.SchemaName);
    }

    [Fact]
    public void RangeOptions_SetPolicyRangePartitionSource()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/PR", "PolicyRangePartition") with
        {
            RangeStart = "2024-01-01",
            RangeEnd = "2025-06-01",
            RangeGranularity = "month"
        });

        var source = Assert.IsType<PolicyRangePartitionSource>(Sales(db).Partitions.Single(p => p.Name == "PR").Source);
        Assert.Equal(new DateTime(2024, 1, 1), source.Start);
        Assert.Equal(new DateTime(2025, 6, 1), source.End);
        Assert.Equal(RefreshGranularityType.Month, source.Granularity);
    }

    [Fact]
    public void RangeGranularity_DefaultsToDay()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales/PR", "PolicyRangePartition") with
        {
            RangeStart = "2024-01-01",
            RangeEnd = "2024-02-01"
        });

        var source = Assert.IsType<PolicyRangePartitionSource>(Sales(db).Partitions.Single(p => p.Name == "PR").Source);
        Assert.Equal(RefreshGranularityType.Day, source.Granularity);
    }

    [Theory]
    [InlineData(null, "2024-02-01")]
    [InlineData("2024-01-01", null)]
    public void MissingRangeDate_Throws(string? start, string? end)
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(
            Add("Sales/PR", "PolicyRangePartition") with { RangeStart = start, RangeEnd = end }));
        Assert.Contains("--range-start and --range-end", ex.Message);
    }

    [Fact]
    public void InvalidRangeDate_Throws()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(
            Add("Sales/PR", "PolicyRangePartition") with { RangeStart = "not-a-date", RangeEnd = "2024-02-01" }));
        Assert.Contains("--range-start", ex.Message);
    }

    [Fact]
    public void InvalidRangeGranularity_Throws()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(
            Add("Sales/PR", "PolicyRangePartition") with
            {
                RangeStart = "2024-01-01",
                RangeEnd = "2024-02-01",
                RangeGranularity = "fortnight"
            }));
        Assert.Contains("Unknown range granularity", ex.Message);
    }

    [Fact]
    public void RangeStartAfterEnd_Throws()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(
            Add("Sales/PR", "PolicyRangePartition") with { RangeStart = "2025-01-01", RangeEnd = "2024-01-01" }));
        Assert.Contains("--range-start must be before --range-end", ex.Message);
    }

    [Fact]
    public void Source_SetsProviderDataSourceProvider()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("DS", "ProviderDataSource") with { Source = "System.Data.SqlClient" });

        var ds = Assert.IsType<ProviderDataSource>(db.Model.DataSources.Single(d => d.Name == "DS"));
        Assert.Equal("System.Data.SqlClient", ds.Provider);
    }

    [Fact]
    public void Endpoint_FlowsIntoProviderConnectionString()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("DS", "ProviderDataSource") with { Endpoint = "myserver.database.windows.net" });

        var ds = Assert.IsType<ProviderDataSource>(db.Model.DataSources.Single(d => d.Name == "DS"));
        Assert.Contains("myserver.database.windows.net", ds.ConnectionString);
    }

    [Fact]
    public void ConnectionString_SetsProviderConnectionString()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("DS", "ProviderDataSource") with { ConnectionString = "Data Source=tcp:host,1433" });

        var ds = Assert.IsType<ProviderDataSource>(db.Model.DataSources.Single(d => d.Name == "DS"));
        Assert.Equal("Data Source=tcp:host,1433", ds.ConnectionString);
    }

    [Fact]
    public void SourceType_SetsStructuredConnectionProtocol()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("DS", "StructuredDataSource") with { SourceType = "tds", Endpoint = "host", SourceDatabase = "db" });

        var ds = Assert.IsType<StructuredDataSource>(db.Model.DataSources.Single(d => d.Name == "DS"));
        Assert.Equal("tds", ds.ConnectionDetails.Protocol);
    }

    private static ModelObjectAddRequest Add(string path, string type)
        => new(path, type, Value: null, [], IfNotExists: false);

    private static Table Sales(Database db) => db.Model.Tables.Single(t => t.Name == "Sales");

    private static Database NewDatabase()
        => new() { Name = "M", Model = new Model { Name = "Model" } };

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
}
