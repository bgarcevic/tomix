using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// An add option supplied for a type that cannot consume it must hard-error
/// (<see cref="UnsupportedAddOptionException"/>) instead of being silently dropped.
/// </summary>
public sealed class TomAddOptionValidationTests
{
    [Theory]
    [InlineData("CalcTable", "CT")]
    [InlineData("CalcGroup", "CG")]
    public void Columns_OnCalcTableOrCalcGroup_Throws(string type, string path)
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add(path, type) with { Columns = "X,Y" }));
        Assert.Contains("--columns", ex.Message);
        Assert.Contains(type, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EntityPartition")]
    [InlineData("PolicyRangePartition")]
    public void PartitionExpression_OnNonMPartition_Throws(string type)
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("Sales/P", type) with { PartitionExpression = "SHOULD BE REJECTED" }));
        Assert.Contains("--partition-expression", ex.Message);
    }

    [Fact]
    public void ConnectionString_OnStructuredDataSource_Throws()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("DS", "StructuredDataSource") with { ConnectionString = "Data Source=x" }));
        Assert.Contains("--connection-string", ex.Message);
    }

    [Fact]
    public void Source_OnStructuredDataSource_Throws()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("DS", "StructuredDataSource") with { Source = "System.Data.SqlClient" }));
        Assert.Contains("--source ", ex.Message);
    }

    [Fact]
    public void SourceDatabase_OnEntityPartition_ThrowsWithSchemaHint()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("Sales/EP", "EntityPartition") with { SourceTable = "Orders", SourceDatabase = "dbo" }));
        Assert.Contains("--source-database", ex.Message);
        Assert.Contains("--source-schema", ex.Message);
    }

    [Fact]
    public void SourceTable_OnTable_Throws()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("Products", "Table") with { SourceTable = "Orders" }));
        Assert.Contains("--source-table", ex.Message);
    }

    [Fact]
    public void RangeStart_OnMeasure_Throws()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("Sales/M", "Measure") with { RangeStart = "2024-01-01" }));
        Assert.Contains("--range-start", ex.Message);
    }

    [Fact]
    public void Mode_OnRelationship_Throws()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        Assert.Throws<UnsupportedAddOptionException>(() => mutator.AddObject(
            Add("Sales[Amount]->Sales[Amount]", "Relationship") with { Mode = "Import" }));
    }

    // Positive controls: options on their consuming types still succeed.

    [Fact]
    public void Columns_OnTable_StillSucceeds()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("Products", "Table") with { Columns = "Sku" });
        Assert.True(result.Changed);
    }

    [Fact]
    public void PartitionExpression_OnCalcTable_StillSucceeds()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("CT", "CalcTable") with { PartitionExpression = "{1}" });
        Assert.True(result.Changed);
    }

    private static ModelObjectAddRequest Add(string path, string type)
        => new(path, type, Value: null, [], IfNotExists: false);

    private static Database NewDatabase()
        => new() { Name = "M", Model = new Model { Name = "Model" } };

    private static Database WithSales()
    {
        var db = NewDatabase();
        var sales = new Table { Name = "Sales" };
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Double, SourceColumn = "Amount" });
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
