using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Measure property coverage for set: the writable scalars beyond the core six (data category,
/// simple-measure flag, lineage tags), with value parsing, the errors for values that cannot
/// apply, and read-back through the summarizer.
/// </summary>
public sealed class TomMeasurePropertyTests
{
    [Fact]
    public void SetProperty_StringProperties_Apply()
    {
        var (mutator, measure) = NewModel();

        mutator.SetProperty(Set("dataCategory", "Time"));
        mutator.SetProperty(Set("lineageTag", "tag-1"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        Assert.Equal("Time", measure.DataCategory);
        Assert.Equal("tag-1", measure.LineageTag);
        Assert.Equal("src-tag-1", measure.SourceLineageTag);
    }

    [Fact]
    public void SetProperty_IsSimpleMeasure_ParsesAndRejects()
    {
        var (mutator, measure) = NewModel();

        mutator.SetProperty(Set("isSimpleMeasure", "true"));

        Assert.True(measure.IsSimpleMeasure);

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("isSimpleMeasure", "maybe")));
        Assert.Contains("true or false", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, measure) = NewModel();

        mutator.SetProperty(Set("dataCategory", "Time"));
        mutator.SetProperty(Set("isSimpleMeasure", "true"));
        mutator.SetProperty(Set("sourceLineageTag", "src-tag-1"));

        var snapshot = TomModelSummarizer.Snapshot((Database)measure.Model.Database, "M");
        var measureObject = snapshot.Objects.Single(o => o.Name == "T").Children.Single(c => c.Name == "M");
        var projected = ModelPropertyCatalog.Project(measureObject);

        Assert.Equal("Time", projected["dataCategory"]);
        Assert.Equal(true, projected["isSimpleMeasure"]);
        Assert.Equal("src-tag-1", projected["sourceLineageTag"]);
    }

    [Fact]
    public void SetProperty_UnknownMeasureProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("isSimpleMeasure", ex.Message);
        Assert.Contains("sourceLineageTag", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("T/M", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Measure);

    private static (TomModelMutator Mutator, Measure Measure) NewModel()
    {
        // Measure.DataCategory (1455+), LineageTag (1540+), and SourceLineageTag (1550+) are
        // compatibility-gated by TOM at set time; 1702 clears all of them.
        var db = NewDatabase(compatibilityLevel: 1702);
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        var measure = new Measure { Name = "M", Expression = "1" };
        table.Measures.Add(measure);
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), measure);
    }
}
