using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// `replace --type` narrows the walk to the matching object kinds: the model-level
/// description is skipped under any filter, `column` still matches calculated columns
/// (same group semantics as ls/find), and `calculatedcolumn` narrows to calculated ones.
/// </summary>
public sealed class TomReplaceKindFilterTests
{
    [Fact]
    public void NoFilter_ReachesEveryKind()
    {
        var db = NewFixture();

        ReplaceDescriptions(db, filter: null);

        Assert.Equal("new model", db.Model.Description);
        Assert.Equal("new table", db.Model.Tables["Sales"].Description);
        Assert.Equal("new measure", db.Model.Tables["Sales"].Measures["Total"].Description);
        Assert.Equal("new data column", db.Model.Tables["Sales"].Columns["Amount"].Description);
        Assert.Equal("new calc column", db.Model.Tables["Sales"].Columns["Margin"].Description);
    }

    [Fact]
    public void MeasureFilter_OnlyTouchesMeasures()
    {
        var db = NewFixture();

        ReplaceDescriptions(db, ModelObjectKind.Measure);

        Assert.Equal("old model", db.Model.Description);
        Assert.Equal("old table", db.Model.Tables["Sales"].Description);
        Assert.Equal("new measure", db.Model.Tables["Sales"].Measures["Total"].Description);
        Assert.Equal("old data column", db.Model.Tables["Sales"].Columns["Amount"].Description);
        Assert.Equal("old calc column", db.Model.Tables["Sales"].Columns["Margin"].Description);
    }

    [Fact]
    public void ColumnFilter_TouchesDataAndCalculatedColumns()
    {
        var db = NewFixture();

        ReplaceDescriptions(db, ModelObjectKind.Column);

        Assert.Equal("old measure", db.Model.Tables["Sales"].Measures["Total"].Description);
        Assert.Equal("new data column", db.Model.Tables["Sales"].Columns["Amount"].Description);
        Assert.Equal("new calc column", db.Model.Tables["Sales"].Columns["Margin"].Description);
    }

    [Fact]
    public void CalculatedColumnFilter_NarrowsToCalculatedColumns()
    {
        var db = NewFixture();

        ReplaceDescriptions(db, ModelObjectKind.CalculatedColumn);

        Assert.Equal("old data column", db.Model.Tables["Sales"].Columns["Amount"].Description);
        Assert.Equal("new calc column", db.Model.Tables["Sales"].Columns["Margin"].Description);
    }

    [Fact]
    public void AnyFilter_SkipsModelLevelSites()
    {
        var db = NewFixture();

        var changes = ReplaceDescriptions(db, ModelObjectKind.Function);

        Assert.Equal(0, changes);
        Assert.Equal("old model", db.Model.Description);
        Assert.Equal("old table", db.Model.Tables["Sales"].Description);
    }

    private static int ReplaceDescriptions(Database db, ModelObjectKind? filter)
    {
        var mutator = new TomModelMutator(db);
        return mutator.ReplaceText(new ModelReplaceRequest(
            "old", "new", Scope: "descriptions", Regex: false, CaseSensitive: false, Apply: true, Type: filter)
        ).ChangeCount;
    }

    private static Database NewFixture()
    {
        var db = TestModels.NewDatabase();
        var sales = TestModels.NewTable("Sales", "Amount");
        sales.Description = "old table";
        sales.Measures.Add(new Measure { Name = "Total", Expression = "1", Description = "old measure" });
        sales.Columns["Amount"].Description = "old data column";
        sales.Columns.Add(new CalculatedColumn
        {
            Name = "Margin",
            DataType = DataType.Double,
            Expression = "1",
            Description = "old calc column"
        });
        db.Model.Tables.Add(sales);
        db.Model.Description = "old model";
        return db;
    }
}
