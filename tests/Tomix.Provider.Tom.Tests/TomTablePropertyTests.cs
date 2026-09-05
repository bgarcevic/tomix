using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Table property coverage for set: the full writable scalar surface (visibility flags, refresh
/// and aggregation exclusions, source precedence, DirectLake indexing, lineage tags), with value
/// parsing, the errors for values that cannot apply, and read-back through the summarizer.
/// </summary>
public sealed class TomTablePropertyTests
{
    [Fact]
    public void SetProperty_BooleanFlags_Apply()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("isPrivate", "true"));
        mutator.SetProperty(Set("excludeFromModelRefresh", "true"));
        mutator.SetProperty(Set("excludeFromAutomaticAggregations", "true"));
        mutator.SetProperty(Set("showAsVariationsOnly", "false"));
        mutator.SetProperty(Set("systemManaged", "false"));

        Assert.True(table.IsPrivate);
        Assert.True(table.ExcludeFromModelRefresh);
        Assert.True(table.ExcludeFromAutomaticAggregations);
        Assert.False(table.ShowAsVariationsOnly);
        Assert.False(table.SystemManaged);
    }

    [Fact]
    public void SetProperty_AlternateSourcePrecedence_ParsesAndRejects()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("alternateSourcePrecedence", "2"));

        Assert.Equal(2, table.AlternateSourcePrecedence);

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("alternateSourcePrecedence", "first")));
        Assert.Contains("integer", ex.Message);
    }

    [Theory]
    [InlineData("directLakeIndexingBehavior", "Explicit")]
    [InlineData("direct lake indexing behavior", "auto")]
    public void SetProperty_DirectLakeIndexingBehavior_ParsesEnum(string property, string value)
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set(property, value));

        Assert.Equal(Enum.Parse<DirectLakeIndexingBehavior>(value, ignoreCase: true), table.DirectLakeIndexingBehavior);
    }

    [Fact]
    public void SetProperty_DirectLakeIndexingBehavior_InvalidValue_ListsValidNames()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("directLakeIndexingBehavior", "Lakes")));

        Assert.Contains("Default", ex.Message);
        Assert.Contains("directLakeIndexingBehavior", ex.Message);
    }

    [Fact]
    public void SetProperty_StringProperties_Apply()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("lineageTag", "tag-1"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        Assert.Equal("tag-1", table.LineageTag);
        Assert.Equal("src-tag-1", table.SourceLineageTag);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("isPrivate", "true"));
        mutator.SetProperty(Set("excludeFromModelRefresh", "true"));
        mutator.SetProperty(Set("alternateSourcePrecedence", "1"));
        mutator.SetProperty(Set("directLakeIndexingBehavior", "Explicit"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        var snapshot = TomModelSummarizer.Snapshot((Database)table.Model.Database, "M");
        var projected = ModelPropertyCatalog.Project(snapshot.Objects.Single(o => o.Name == "T"));

        Assert.Equal(true, projected["isPrivate"]);
        Assert.Equal(true, projected["excludeFromModelRefresh"]);
        Assert.Equal(1, projected["alternateSourcePrecedence"]);
        Assert.Equal("Explicit", projected["directLakeIndexingBehavior"]);
        Assert.Equal("src-tag-1", projected["sourceLineageTag"]);
    }

    [Fact]
    public void SetProperty_UnknownTableProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("excludeFromModelRefresh", ex.Message);
        Assert.Contains("directLakeIndexingBehavior", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("tables/T", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Table);

    private static (TomModelMutator Mutator, Table Table) NewModel()
    {
        // DirectLakeIndexingBehavior's non-Default values are gated behind the preview
        // compatibility sentinel, which real DirectLake models run at.
        var db = NewDatabase(compatibilityLevel: 1_000_000);
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), table);
    }
}
