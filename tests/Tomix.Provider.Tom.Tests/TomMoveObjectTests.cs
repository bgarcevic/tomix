using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Cross-table measure moves. The measure instance is detached and re-added (not cloned), so
/// child objects (KPI) and object-identity references (translations) must survive, and
/// perspective membership — which hangs off the table entry — must re-home to the target table.
/// </summary>
public sealed class TomMoveObjectTests
{
    [Fact]
    public void MoveMeasure_MovesBetweenTables()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        var result = mutator.MoveObject(Move("Sales/Revenue", "Metrics"));

        Assert.True(result.Changed);
        Assert.Equal("Metrics/Revenue", result.Path);
        Assert.Empty(db.Model.Tables["Sales"].Measures);
        var moved = Assert.Single(db.Model.Tables["Metrics"].Measures);
        Assert.Equal("Revenue", moved.Name);
        Assert.Equal("SUM('Sales'[Amount])", moved.Expression);
    }

    [Fact]
    public void MoveMeasure_WithRename()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        var result = mutator.MoveObject(Move("Sales/Revenue", "Metrics", "Total Revenue"));

        Assert.Equal("Metrics/Total Revenue", result.Path);
        Assert.Equal("Total Revenue", Assert.Single(db.Model.Tables["Metrics"].Measures).Name);
    }

    [Fact]
    public void MoveMeasure_KeepsKpi()
    {
        var db = BaseModel();
        db.Model.Tables["Sales"].Measures["Revenue"].KPI = new KPI { TargetExpression = "100" };
        var mutator = new TomModelMutator(db);

        mutator.MoveObject(Move("Sales/Revenue", "Metrics"));

        Assert.Equal("100", db.Model.Tables["Metrics"].Measures["Revenue"].KPI.TargetExpression);
    }

    [Fact]
    public void MoveMeasure_RehomesPerspectiveMembership()
    {
        var db = BaseModel();
        var perspective = new Perspective { Name = "Reporting" };
        var entry = new PerspectiveTable { Table = db.Model.Tables["Sales"] };
        entry.PerspectiveMeasures.Add(new PerspectiveMeasure { Measure = db.Model.Tables["Sales"].Measures["Revenue"] });
        perspective.PerspectiveTables.Add(entry);
        db.Model.Perspectives.Add(perspective);
        var mutator = new TomModelMutator(db);

        mutator.MoveObject(Move("Sales/Revenue", "Metrics"));

        var oldEntry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table.Name == "Sales");
        Assert.Empty(oldEntry!.PerspectiveMeasures);
        var newEntry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table.Name == "Metrics");
        Assert.Equal("Revenue", Assert.Single(newEntry!.PerspectiveMeasures).Measure.Name);
    }

    [Fact]
    public void MoveMeasure_RepointsTranslations()
    {
        var db = BaseModel();
        var culture = new Culture { Name = "da-DK" };
        culture.ObjectTranslations.Add(new ObjectTranslation
        {
            Object = db.Model.Tables["Sales"].Measures["Revenue"],
            Property = TranslatedProperty.Caption,
            Value = "Omsætning"
        });
        db.Model.Cultures.Add(culture);
        var mutator = new TomModelMutator(db);

        mutator.MoveObject(Move("Sales/Revenue", "Metrics"));

        var translation = Assert.Single(culture.ObjectTranslations);
        Assert.Equal("Omsætning", translation.Value);
        Assert.Same(db.Model.Tables["Metrics"].Measures["Revenue"], translation.Object);
    }

    [Fact]
    public void MoveMeasure_WithDisplayFolder_SetsFolderOnTheMovedMeasure()
    {
        var db = BaseModel();
        db.Model.Tables["Sales"].Measures["Revenue"].DisplayFolder = "Old";
        var mutator = new TomModelMutator(db);

        mutator.MoveObject(new ModelObjectMoveRequest(
            "Sales/Revenue", Type: null, "Metrics", NewName: null, NewDisplayFolder: @"Finance\KPIs"));

        Assert.Equal(@"Finance\KPIs", db.Model.Tables["Metrics"].Measures["Revenue"].DisplayFolder);
    }

    [Fact]
    public void MoveMeasure_WithoutDisplayFolder_KeepsTheFolderItHad()
    {
        var db = BaseModel();
        db.Model.Tables["Sales"].Measures["Revenue"].DisplayFolder = "Finance";
        var mutator = new TomModelMutator(db);

        mutator.MoveObject(Move("Sales/Revenue", "Metrics"));

        Assert.Equal("Finance", db.Model.Tables["Metrics"].Measures["Revenue"].DisplayFolder);
    }

    [Fact]
    public void MoveColumn_IsNotSupported()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        Assert.Throws<NotSupportedException>(() => mutator.MoveObject(Move("Sales/Amount", "Metrics")));
    }

    [Fact]
    public void MoveMeasure_ToMissingTable_Throws()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.MoveObject(Move("Sales/Revenue", "Nope")));

        Assert.Contains("Nope", ex.Message);
        Assert.Single(db.Model.Tables["Sales"].Measures);
    }

    [Fact]
    public void MoveMeasure_NameCollisionInTarget_Throws()
    {
        var db = BaseModel();
        db.Model.Tables["Metrics"].Measures.Add(new Measure { Name = "Revenue", Expression = "0" });
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.MoveObject(Move("Sales/Revenue", "Metrics")));

        Assert.Contains("already exists in table 'Metrics'", ex.Message);
        Assert.Single(db.Model.Tables["Sales"].Measures);
    }

    private static ModelObjectMoveRequest Move(string path, string newParent, string? newName = null)
        => new(path, Type: null, newParent, newName);

    private static Database BaseModel()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };

        var sales = NewTable("Sales", "Amount");
        sales.Measures.Add(new Measure { Name = "Revenue", Expression = "SUM('Sales'[Amount])" });
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(NewTable("Metrics", "Dummy"));

        return db;
    }

    private static Table NewTable(string name, params string[] columns)
    {
        var table = new Table { Name = name };
        foreach (var column in columns)
            table.Columns.Add(new DataColumn { Name = column, DataType = DataType.Int64, SourceColumn = column });
        table.Partitions.Add(new Partition
        {
            Name = name,
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        return table;
    }
}
