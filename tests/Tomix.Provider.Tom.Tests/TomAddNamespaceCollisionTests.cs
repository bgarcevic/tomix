using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Measures, columns, and hierarchies share one namespace within a table; the engine rejects a
/// model where two of them carry the same name. Adds must fail up front instead of writing TMDL
/// a deploy won't accept (live QA: a measure added over a same-named column then triggered
/// ambiguous resolution in <c>tx set</c>).
/// </summary>
public sealed class TomAddNamespaceCollisionTests
{
    [Fact]
    public void AddMeasure_Fails_WhenColumnHasSameName()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Columns.Add(new DataColumn { Name = "Status", DataType = DataType.String });

        var ex = Assert.Throws<InvalidOperationException>(() => Add(db, "measure", "Sales/Status"));
        Assert.Contains("column named 'Status'", ex.Message);
        Assert.Contains("share a namespace", ex.Message);
    }

    [Fact]
    public void AddMeasure_Fails_WhenHierarchyHasSameName()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Hierarchies.Add(new Hierarchy { Name = "Status" });

        var ex = Assert.Throws<InvalidOperationException>(() => Add(db, "measure", "Sales/Status"));
        Assert.Contains("hierarchy named 'Status'", ex.Message);
    }

    [Theory]
    [InlineData("calccolumn")]
    [InlineData("datacolumn")]
    public void AddColumn_Fails_WhenMeasureHasSameName(string type)
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Measures.Add(new Measure { Name = "Status", Expression = "1" });

        var ex = Assert.Throws<InvalidOperationException>(() => Add(db, type, "Sales/Status"));
        Assert.Contains("measure named 'Status'", ex.Message);
    }

    [Fact]
    public void AddHierarchy_Fails_WhenColumnHasSameName()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Columns.Add(new DataColumn { Name = "Status", DataType = DataType.String });

        var ex = Assert.Throws<InvalidOperationException>(() => Add(db, "hierarchy", "Sales/Status"));
        Assert.Contains("column named 'Status'", ex.Message);
    }

    [Fact]
    public void AddMeasure_CollisionIsCaseInsensitive()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Columns.Add(new DataColumn { Name = "Status", DataType = DataType.String });

        Assert.Throws<InvalidOperationException>(() => Add(db, "measure", "Sales/STATUS"));
    }

    [Fact]
    public void AddMeasure_IfNotExists_StillFails_OnCrossKindCollision()
    {
        // --if-not-exists tolerates an existing MEASURE of the same name; a column squatting on
        // the name is not the requested object, so it must remain a hard error.
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Columns.Add(new DataColumn { Name = "Status", DataType = DataType.String });

        Assert.Throws<InvalidOperationException>(() => Add(db, "measure", "Sales/Status", ifNotExists: true));
    }

    [Fact]
    public void AddMeasure_IfNotExists_StillReturnsUnchanged_OnSameKindDuplicate()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Sales");
        table.Measures.Add(new Measure { Name = "Status", Expression = "1" });

        var result = Add(db, "measure", "Sales/Status", ifNotExists: true);

        Assert.False(result.Changed);
    }

    [Fact]
    public void AddMeasure_Succeeds_WhenNameIsFree()
    {
        var db = NewDatabase();
        AddTable(db, "Sales");

        var result = Add(db, "measure", "Sales/Status");

        Assert.True(result.Changed);
    }

    private static ModelObjectMutationResult Add(Database db, string type, string path, bool ifNotExists = false)
        => new TomModelMutator(db).AddObject(new ModelObjectAddRequest(
            path,
            Type: type,
            Value: "1",
            Properties: [],
            IfNotExists: ifNotExists));

    private static Database NewDatabase()
        => new() { Name = "M", Model = new Model { Name = "Model" } };

    private static Table AddTable(Database db, string name)
    {
        var table = new Table { Name = name };
        table.Partitions.Add(new Partition
        {
            Name = name,
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        db.Model.Tables.Add(table);
        return table;
    }
}
