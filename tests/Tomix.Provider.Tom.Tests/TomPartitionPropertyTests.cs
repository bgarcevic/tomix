using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Partition property coverage for set: the writable scalars beyond name/expression
/// (description, storage mode, data view, query group, hybrid recalc retention), the
/// source-bound guards (expression is M-source-only, retainDataTillForceCalculate is
/// calculated-source-only), and read-back through the summarizer.
/// </summary>
public sealed class TomPartitionPropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, partition, calcPartition) = NewModel();

        mutator.SetProperty(Set("T/T", "description", "described"));
        mutator.SetProperty(Set("T/T", "mode", "Dual"));
        mutator.SetProperty(Set("T/T", "dataView", "Full"));
        mutator.SetProperty(Set("T/Calc", "retainDataTillForceCalculate", "true"));

        Assert.Equal("described", partition.Description);
        Assert.Equal(ModeType.Dual, partition.Mode);
        Assert.Equal(DataViewType.Full, partition.DataView);
        Assert.True(Assert.IsType<CalculatedPartitionSource>(calcPartition.Source).RetainDataTillForceCalculate);
    }

    [Fact]
    public void SetProperty_Mode_RejectsUnknownEnumName()
    {
        var (mutator, _, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("T/T", "mode", "Sometimes")));

        Assert.Contains("must be one of: Import, DirectQuery, Default, Push, Dual, DirectLake", ex.Message);
    }

    [Fact]
    public void SetProperty_QueryGroup_ResolvesByNameClearsOnEmptyAndErrorsOnMissing()
    {
        var (mutator, partition, _) = NewModel();

        mutator.SetProperty(Set("T/T", "queryGroup", "QG"));
        Assert.Equal("QG", partition.QueryGroup?.Name);

        mutator.SetProperty(Set("T/T", "queryGroup", ""));
        Assert.Null(partition.QueryGroup);

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("T/T", "queryGroup", "Missing")));
        Assert.Contains("Query group 'Missing' does not exist in the model", ex.Message);
    }

    [Fact]
    public void SetProperty_SourceBoundProperties_StickToTheirSourceKind()
    {
        var (mutator, partition, _) = NewModel();

        mutator.SetProperty(Set("T/T", "expression", "let y = 2 in y"));
        Assert.Equal("let y = 2 in y", Assert.IsType<MPartitionSource>(partition.Source).Expression);

        var expressionEx = Assert.Throws<NotSupportedException>(
            () => mutator.SetProperty(Set("T/Calc", "expression", "T3")));
        Assert.Contains("only supported for partitions with an M source", expressionEx.Message);

        var retainEx = Assert.Throws<NotSupportedException>(
            () => mutator.SetProperty(Set("T/T", "retainDataTillForceCalculate", "true")));
        Assert.Contains("only supported for partitions with a calculated source", retainEx.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, partition, _) = NewModel();

        mutator.SetProperty(Set("T/T", "mode", "Dual"));
        mutator.SetProperty(Set("T/T", "dataView", "Full"));
        mutator.SetProperty(Set("T/T", "queryGroup", "QG"));
        mutator.SetProperty(Set("T/Calc", "retainDataTillForceCalculate", "true"));

        var snapshot = TomModelSummarizer.Snapshot((Database)partition.Model.Database, "M");
        var children = snapshot.Objects.Single(o => o.Name == "T").Children;
        var projected = ModelPropertyCatalog.Project(children.Single(c => c.Name == "T"));
        var calcProjected = ModelPropertyCatalog.Project(children.Single(c => c.Name == "Calc"));

        Assert.Equal("dual", projected["mode"]);
        Assert.Equal("Full", projected["dataView"]);
        Assert.Equal("QG", projected["queryGroup"]);
        Assert.Equal("calculated", calcProjected["mode"]);
        Assert.Equal(true, calcProjected["retainDataTillForceCalculate"]);
    }

    [Fact]
    public void SetProperty_UnknownPartitionProperty_HintListsWritableSet()
    {
        var (mutator, _, _) = NewModel();

        // On a calculated source the hint lists every token except the M-source-only one.
        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("T/Calc", "bogus", "x")));

        Assert.Contains("retainDataTillForceCalculate", ex.Message);
        Assert.Contains("queryGroup", ex.Message);
        Assert.DoesNotContain("expression", ex.Message);
    }

    private static ModelObjectSetRequest Set(string path, string property, string value)
        => new(path, [new ModelPropertyAssignment(property, value)], ModelObjectKind.Partition);

    private static (TomModelMutator Mutator, Partition Partition, Partition CalcPartition) NewModel()
    {
        // ModeType.Dual (1455+), RetainDataTillForceCalculate (1400+), and QueryGroup (1480+)
        // are compatibility-gated by TOM at set time; 1702 clears all of them.
        var db = NewDatabase(compatibilityLevel: 1702);
        var table = new Table { Name = "T" };
        var partition = new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        };
        table.Partitions.Add(partition);
        var calcPartition = new Partition
        {
            Name = "Calc",
            Source = new CalculatedPartitionSource { Expression = "T2" }
        };
        table.Partitions.Add(calcPartition);
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        db.Model.Tables.Add(table);
        // The query group's name is derived from its folder path; TOM refuses to set Name.
        db.Model.QueryGroups.Add(new QueryGroup { Folder = "QG" });
        return (new TomModelMutator(db), partition, calcPartition);
    }
}
