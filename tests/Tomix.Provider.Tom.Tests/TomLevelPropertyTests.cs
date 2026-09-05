using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Level property coverage for set: ordinal repositioning and lineage tags, with the errors for
/// values that cannot apply and read-back through the summarizer (levels are reached through
/// their hierarchy's children).
/// </summary>
public sealed class TomLevelPropertyTests
{
    [Fact]
    public void SetProperty_NewProperties_Apply()
    {
        var (mutator, level) = NewModel();

        mutator.SetProperty(Set("ordinal", "2"));
        mutator.SetProperty(Set("lineageTag", "tag-1"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        Assert.Equal(2, level.Ordinal);
        Assert.Equal("tag-1", level.LineageTag);
        Assert.Equal("src-tag-1", level.SourceLineageTag);
    }

    [Fact]
    public void SetProperty_Ordinal_RejectsNonInteger()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("ordinal", "top")));

        Assert.Contains("must be an integer", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, level) = NewModel();

        mutator.SetProperty(Set("ordinal", "2"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        var snapshot = TomModelSummarizer.Snapshot((Database)level.Model.Database, "M");
        var hierarchyObject = snapshot.Objects.Single(o => o.Name == "T").Children.Single(c => c.Name == "H");
        var levelObject = hierarchyObject.Children.Single(c => c.Kind == ModelObjectKind.Level && c.Name == "L1");
        var projected = ModelPropertyCatalog.Project(levelObject);

        Assert.Equal(2, projected["ordinal"]);
        Assert.Equal("src-tag-1", projected["sourceLineageTag"]);
    }

    [Fact]
    public void SetProperty_UnknownLevelProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("ordinal", ex.Message);
        Assert.Contains("sourceLineageTag", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("T/H/L1", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Level);

    private static (TomModelMutator Mutator, Level Level) NewModel()
    {
        // Level.LineageTag (1540+) and SourceLineageTag (1550+) are compatibility-gated by TOM
        // at set time; 1702 clears both (Ordinal is ungated).
        var db = NewDatabase(compatibilityLevel: 1702);
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C1", DataType = DataType.String });
        table.Columns.Add(new DataColumn { Name = "C2", DataType = DataType.String });
        var hierarchy = new Hierarchy { Name = "H" };
        var level = new Level { Name = "L1", Column = table.Columns["C1"] };
        hierarchy.Levels.Add(level);
        hierarchy.Levels.Add(new Level { Name = "L2", Column = table.Columns["C2"] });
        table.Hierarchies.Add(hierarchy);
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), level);
    }
}
