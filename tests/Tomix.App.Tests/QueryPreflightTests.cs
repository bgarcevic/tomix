using Tomix.App.Query;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class QueryPreflightTests
{
    [Theory]
    [InlineData("EVALUATE Sales")]
    [InlineData("EVALUATE 'Sales'")]
    [InlineData("EVALUATE ROW(\"x\", SUM(Sales[Amount]))")]
    [InlineData("EVALUATE ROW(\"x\", SUM('Sales'[Amount]), \"y\", [Total Sales])")]
    [InlineData("EVALUATE ROW(\"x\", 'Sales'[Total Sales])")]
    [InlineData("EVALUATE SUMMARIZECOLUMNS(Product[Color], \"Total\", [Total Sales]) ORDER BY [Total] DESC")]
    [InlineData("EVALUATE FILTER(ADDCOLUMNS(Sales, \"Twice\", Sales[Amount] * 2), [Twice] > 10)")]
    [InlineData("EVALUATE SELECTCOLUMNS(Product, \"c\", [Color])")]
    [InlineData("DEFINE MEASURE Sales[Margin] = [Total Sales] * 0.1 EVALUATE ROW(\"m\", [Margin], \"n\", Sales[Margin])")]
    [InlineData("DEFINE COLUMN Sales[Double] = Sales[Amount] * 2 EVALUATE SELECTCOLUMNS(Sales, \"d\", Sales[Double])")]
    [InlineData("DEFINE TABLE Top5 = TOPN(5, Sales) EVALUATE SELECTCOLUMNS(Top5, \"a\", Top5[Anything], \"b\", 'Top5'[Other])")]
    [InlineData("DEFINE VAR Big = FILTER(Sales, Sales[Amount] > @min) EVALUATE Big")]
    [InlineData("EVALUATE ROW(\"Sales[Amout]\", 1) // [Totl Sales] in a comment")]
    [InlineData("EVALUATE {1, 2, 3}")]
    [InlineData("EVALUATE TOTALYTD([Total Sales], 'Fiscal')")]
    public void Check_FindsNothing_WhenEveryReferenceResolves(string query)
    {
        Assert.Empty(QueryPreflight.Check(query, Model()));
    }

    [Theory]
    [InlineData("EVALUATE ROW(\"x\", SUM(Sales[Amout]))", "Sales[Amout]", "Amount")]
    [InlineData("EVALUATE ROW(\"x\", SUM(Sale[Amount]))", "Sale[Amount]", "Sales")]
    [InlineData("EVALUATE 'Prodct'", "'Prodct'", "Product")]
    [InlineData("EVALUATE ROW(\"x\", [Total Sale])", "[Total Sale]", "Total Sales")]
    [InlineData("EVALUATE ROW(\"x\", Sales[Totl Sales])", "Sales[Totl Sales]", "Total Sales")]
    public void Check_ReportsMiss_WithClosestName(string query, string reference, string suggestion)
    {
        var miss = Assert.Single(QueryPreflight.Check(query, Model()));

        Assert.Equal(reference, miss.Reference);
        Assert.Equal(suggestion, miss.Suggestion);
        Assert.Contains($"Did you mean '{suggestion}'?", miss.Hint);
    }

    [Fact]
    public void Check_ListsSmallTableMembers_WhenNothingIsClose()
    {
        var miss = Assert.Single(QueryPreflight.Check("EVALUATE VALUES(Product[Zzzzzzzz])", Model()));

        Assert.Null(miss.Suggestion);
        Assert.Contains("Category", miss.Hint);
        Assert.Contains("Color", miss.Hint);
    }

    [Fact]
    public void Check_ListsModelTables_WhenTableIsNotClose()
    {
        var miss = Assert.Single(QueryPreflight.Check("EVALUATE 'Zzzzzzzzzz'", Model()));

        Assert.Null(miss.Suggestion);
        Assert.Contains("Product", miss.Hint);
        Assert.Contains("Sales", miss.Hint);
    }

    [Fact]
    public void Check_ReportsEveryMissOnce_WithItsLine()
    {
        var misses = QueryPreflight.Check(
            "EVALUATE\nROW(\"a\", SUM(Sales[Amout]),\n    \"b\", SUM(Sales[Amout]), \"c\", [Total Sale])",
            Model());

        Assert.Collection(misses,
            m => Assert.Equal(("Sales[Amout]", 2), (m.Reference, m.Line)),
            m => Assert.Equal(("[Total Sale]", 3), (m.Reference, m.Line)));
    }

    [Theory]
    [InlineData("EVALUATE FILTER(Sales, Sales[Amout] > 1")] // unbalanced: the server reports syntax
    [InlineData("SELECT * FROM $SYSTEM.TMSCHEMA_TABLES")] // DMV
    public void Check_StaysQuiet_WhenTheQueryIsNotCheckable(string query)
    {
        Assert.Empty(QueryPreflight.Check(query, Model()));
    }

    [Fact]
    public void Check_StaysQuiet_OnAModelWithNoTables()
    {
        Assert.Empty(QueryPreflight.Check("EVALUATE ROW(\"x\", SUM(Sales[Amout]))", new ModelSnapshot("empty", 1601, [])));
    }

    internal static ModelSnapshot Model()
    {
        var sales = Table("Sales",
            Column("Sales", "Amount"),
            Column("Sales", "Quantity"),
            Measure("Sales", "Total Sales", "SUM(Sales[Amount])"));
        var product = Table("Product",
            Column("Product", "Color"),
            Column("Product", "Category"));
        var fiscal = new ModelObject("Fiscal", ModelObjectKind.Calendar, "Fiscal",
            null, null, null, false, null, []);
        return new ModelSnapshot("test", 1601, [sales, product, fiscal]);
    }

    private static ModelObject Table(string name, params ModelObject[] children)
        => new(name, ModelObjectKind.Table, name, "regular", null, null, false, null, children);

    private static ModelObject Column(string table, string name)
        => new(name, ModelObjectKind.Column, $"{table}/{name}", "string", null, null, false, name, []);

    private static ModelObject Measure(string table, string name, string expression)
        => new(name, ModelObjectKind.Measure, $"{table}/{name}", null, expression, null, false, null, []);
}
