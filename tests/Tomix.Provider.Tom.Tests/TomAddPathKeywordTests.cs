using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Verifies that <c>tx add</c> infers the object type from container keywords in the path
/// (e.g. <c>tables/Sales/measures/Revenue</c>) so <c>-t</c> is optional for the common forms.
/// </summary>
public sealed class TomAddPathKeywordTests
{
    [Fact]
    public void MeasureKeyword_InfersTypeAndStripsKeywords()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("tables/Sales/measures/Revenue", Type: null));

        Assert.True(result.Changed);
        Assert.Equal("Sales/Revenue", result.Path);
        var measure = Sales(db).Measures.Single(m => m.Name == "Revenue");
        Assert.Equal("CALCULATE(1)", measure.Expression);
    }

    [Fact]
    public void TableKeyword_InfersType()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("tables/Products", Type: null));

        Assert.True(result.Changed);
        Assert.Equal("Products", result.Path);
        Assert.Contains(db.Model.Tables, t => t.Name == "Products");
    }

    [Fact]
    public void HierarchyLevelKeyword_InfersLevelFromDeepestKeyword()
    {
        var db = WithSalesAndHierarchy();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("tables/Sales/hierarchies/H/levels/L2", Type: null, Value: "Amount"));

        Assert.True(result.Changed);
        Assert.Equal("Sales/H/L2", result.Path);
    }

    [Fact]
    public void RoleMemberKeyword_InfersMember()
    {
        var db = WithRole();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("roles/Admin/members/user@org.com", Type: null));

        Assert.True(result.Changed);
        Assert.Equal("Admin/user@org.com", result.Path);
    }

    [Fact]
    public void ExplicitType_OverridesKeywordButStillStripsKeywords()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        // Explicit -t Measure wins; keywords are still stripped so the path shape is correct.
        var result = mutator.AddObject(Add("tables/Sales/measures/Revenue", Type: "Measure"));

        Assert.True(result.Changed);
        Assert.Equal("Sales/Revenue", result.Path);
    }

    [Fact]
    public void PlainPath_WithoutKeywordOrType_ThrowsActionableError()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(Add("Sales/Revenue", Type: null)));
        Assert.Contains("No object type given", ex.Message);
        Assert.Contains("-t", ex.Message);
    }

    [Fact]
    public void MeasureKeywordWithoutTable_ThrowsActionableError()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            mutator.AddObject(Add("measures/Revenue", Type: null)));
        Assert.Contains("table parent", ex.Message);
    }

    [Fact]
    public void ExpressionKeyword_InfersType()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("expressions/MyParam", Type: null, Value: "1 meta [IsParameterQuery=false]"));

        Assert.True(result.Changed);
        Assert.Contains(db.Model.Expressions, e => e.Name == "MyParam");
    }

    [Fact]
    public void CalcGroupKeyword_InfersType()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("calcgroups/TimeIntel", Type: null, Value: null));

        Assert.True(result.Changed);
        Assert.Contains(db.Model.Tables, t => t.Name == "TimeIntel" && t.CalculationGroup is not null);
    }

    [Fact]
    public void CalcItemKeyword_InfersType()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);
        mutator.AddObject(Add("calcgroups/TimeIntel", Type: null, Value: null));

        var result = mutator.AddObject(Add("calcitems/TimeIntel/YTD", Type: null, Value: "SELECTEDMEASURE()"));

        Assert.True(result.Changed);
        var cg = db.Model.Tables.Single(t => t.Name == "TimeIntel");
        Assert.Contains(cg.CalculationGroup!.CalculationItems, i => i.Name == "YTD");
    }

    [Fact]
    public void KpiKeyword_InfersType()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);
        Sales(db).Measures.Add(new Measure { Name = "Rev", Expression = "1" });

        var result = mutator.AddObject(Add("kpis/Sales/Rev", Type: null, Value: "0"));

        Assert.True(result.Changed);
        Assert.NotNull(Sales(db).Measures.Single(m => m.Name == "Rev").KPI);
    }

    [Theory]
    [InlineData("calculatedtable", "{1}")]
    [InlineData("calculationgroup", null)]
    public void LongFormTypeAliases_ResolveToBuilders(string type, string? value)
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("T1", type, value));

        Assert.True(result.Changed);
        Assert.Contains(db.Model.Tables, t => t.Name == "T1");
    }

    [Fact]
    public void LongFormCalculatedColumnAlias_ResolvesToCalcColumn()
    {
        var db = WithSales();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("Sales/CC", "calculatedcolumn", "Sales[Amount]"));

        Assert.True(result.Changed);
        Assert.Contains(Sales(db).Columns, c => c.Name == "CC" && c is CalculatedColumn);
    }

    [Fact]
    public void DataSourcesKeyword_StillRequiresExplicitType()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        // Provider vs Structured is ambiguous, so datasources/ does not infer a type.
        var ex = Assert.Throws<ArgumentException>(() =>
            mutator.AddObject(Add("datasources/DS", Type: null, Value: null)));
        Assert.Contains("No object type given", ex.Message);
    }

    [Fact]
    public void QuotedKeywordSegment_IsTreatedAsLiteralName()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        // A quoted 'Tables' is a literal table name, not a keyword — so no type is inferred
        // and the call fails with the no-type error rather than creating a table.
        var ex = Assert.Throws<ArgumentException>(() =>
            mutator.AddObject(Add("'Tables'/Foo", Type: null)));
        Assert.Contains("No object type given", ex.Message);
    }

    private static ModelObjectAddRequest Add(string path, string? Type, string? Value = "CALCULATE(1)")
        => new(path, Type, Value, [], IfNotExists: false);

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
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Int64, SourceColumn = "Amount" });
        db.Model.Tables.Add(sales);
        return db;
    }

    private static Database WithSalesAndHierarchy()
    {
        var db = WithSales();
        var sales = Sales(db);
        var hierarchy = new Hierarchy { Name = "H" };
        hierarchy.Levels.Add(new Level { Name = "L1", Ordinal = 0, Column = sales.Columns["Amount"] });
        sales.Hierarchies.Add(hierarchy);
        return db;
    }

    private static Database WithRole()
    {
        var db = NewDatabase();
        db.Model.Roles.Add(new ModelRole { Name = "Admin", ModelPermission = ModelPermission.Read });
        return db;
    }
}
