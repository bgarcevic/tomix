using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// KPI property coverage for set: the presentation scalars (status/trend graphics, descriptions)
/// alongside the existing expression and format-string surface, applied through both the kind
/// hint and the /KPI path, with read-back through the summarizer's KPI child object.
/// </summary>
public sealed class TomKpiPropertyTests
{
    [Fact]
    public void SetProperty_PresentationStrings_ApplyViaKindHint()
    {
        var (mutator, kpi) = NewModel();

        mutator.SetProperty(Set("statusGraphic", "Cylinder"));
        mutator.SetProperty(Set("trendGraphic", "Standard Arrow"));
        mutator.SetProperty(Set("statusDescription", "status"));
        mutator.SetProperty(Set("targetDescription", "target"));
        mutator.SetProperty(Set("trendDescription", "trend"));

        Assert.Equal("Cylinder", kpi.StatusGraphic);
        Assert.Equal("Standard Arrow", kpi.TrendGraphic);
        Assert.Equal("status", kpi.StatusDescription);
        Assert.Equal("target", kpi.TargetDescription);
        Assert.Equal("trend", kpi.TrendDescription);
    }

    [Fact]
    public void SetProperty_StatusGraphic_AppliesViaKpiPath()
    {
        var (mutator, kpi) = NewModel();

        mutator.SetProperty(new ModelObjectSetRequest(
            "T/M/KPI", [new ModelPropertyAssignment("statusGraphic", "Traffic Light")], null));

        Assert.Equal("Traffic Light", kpi.StatusGraphic);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, kpi) = NewModel();

        mutator.SetProperty(Set("statusGraphic", "Cylinder"));
        mutator.SetProperty(Set("trendDescription", "trend"));

        var snapshot = TomModelSummarizer.Snapshot((Database)kpi.Model.Database, "M");
        var kpiObject = snapshot.Objects.Single(o => o.Name == "T").Children
            .Single(c => c.Name == "M").Children.Single(c => c.Kind == ModelObjectKind.Kpi);
        var projected = ModelPropertyCatalog.Project(kpiObject);

        Assert.Equal("Cylinder", projected["statusGraphic"]);
        Assert.Equal("trend", projected["trendDescription"]);
    }

    [Fact]
    public void SetProperty_UnknownKpiProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogusGraphic", "x")));

        Assert.Contains("statusGraphic", ex.Message);
        Assert.Contains("trendDescription", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("T/M", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Kpi);

    private static (TomModelMutator Mutator, KPI Kpi) NewModel()
    {
        var db = NewDatabase();
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        var measure = new Measure { Name = "M", Expression = "1" };
        measure.KPI = new KPI { TargetExpression = "0", StatusExpression = "0" };
        table.Measures.Add(measure);
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), measure.KPI);
    }
}
