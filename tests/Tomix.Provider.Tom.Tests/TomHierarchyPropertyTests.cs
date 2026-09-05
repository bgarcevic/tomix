using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Hierarchy property coverage for set: the writable scalars beyond the core four (member
/// hiding, lineage tags), with enum parsing, the errors for values that cannot apply, and
/// read-back through the summarizer.
/// </summary>
public sealed class TomHierarchyPropertyTests
{
    [Fact]
    public void SetProperty_NewProperties_Apply()
    {
        var (mutator, hierarchy) = NewModel();

        mutator.SetProperty(Set("hideMembers", "HideBlankMembers"));
        mutator.SetProperty(Set("lineageTag", "tag-1"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        Assert.Equal(HierarchyHideMembersType.HideBlankMembers, hierarchy.HideMembers);
        Assert.Equal("tag-1", hierarchy.LineageTag);
        Assert.Equal("src-tag-1", hierarchy.SourceLineageTag);
    }

    [Fact]
    public void SetProperty_HideMembers_RejectsUnknownEnumName()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("hideMembers", "Sometimes")));

        Assert.Contains("must be one of: Default, HideBlankMembers", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, hierarchy) = NewModel();

        mutator.SetProperty(Set("hideMembers", "HideBlankMembers"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        var snapshot = TomModelSummarizer.Snapshot((Database)hierarchy.Model.Database, "M");
        var hierarchyObject = snapshot.Objects.Single(o => o.Name == "T").Children.Single(c => c.Name == "H");
        var projected = ModelPropertyCatalog.Project(hierarchyObject);

        Assert.Equal("HideBlankMembers", projected["hideMembers"]);
        Assert.Equal("src-tag-1", projected["sourceLineageTag"]);
    }

    [Fact]
    public void SetProperty_UnknownHierarchyProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("hideMembers", ex.Message);
        Assert.Contains("sourceLineageTag", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("T/H", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Hierarchy);

    private static (TomModelMutator Mutator, Hierarchy Hierarchy) NewModel()
    {
        // Hierarchy.HideMembers (1400+), LineageTag (1540+), and SourceLineageTag (1550+) are
        // compatibility-gated by TOM at set time; 1702 clears all of them.
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
        hierarchy.Levels.Add(new Level { Name = "L1", Column = table.Columns["C1"] });
        hierarchy.Levels.Add(new Level { Name = "L2", Column = table.Columns["C2"] });
        table.Hierarchies.Add(hierarchy);
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), hierarchy);
    }
}
