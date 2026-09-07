using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Model-root property coverage for set: the "." path reaches the Database and its Model,
/// carrying the compatibility level, culture/collation, the discourage flags, default
/// mode/data view, parallelism caps, and the query-culture/unique-name switches. Read-back
/// rides the snapshot's model-level properties bag.
/// </summary>
public sealed class TomModelRootPropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, database) = NewModel();

        mutator.SetProperty(Set("culture", "en-US"));
        mutator.SetProperty(Set("collation", "Latin1_General_BIN"));
        mutator.SetProperty(Set("discourageImplicitMeasures", "true"));
        mutator.SetProperty(Set("discourageCompositeModels", "true"));
        mutator.SetProperty(Set("defaultMode", "Dual"));
        mutator.SetProperty(Set("defaultDataView", "Full"));
        mutator.SetProperty(Set("maxParallelismPerQuery", "2"));
        mutator.SetProperty(Set("maxParallelismPerRefresh", "1"));
        mutator.SetProperty(Set("sourceQueryCulture", "en-US"));
        mutator.SetProperty(Set("forceUniqueNames", "true"));
        mutator.SetProperty(Set("description", "described"));

        var model = database.Model;
        Assert.Equal("en-US", model.Culture);
        Assert.Equal("Latin1_General_BIN", model.Collation);
        Assert.True(model.DiscourageImplicitMeasures);
        Assert.True(model.DiscourageCompositeModels);
        Assert.Equal(ModeType.Dual, model.DefaultMode);
        Assert.Equal(DataViewType.Full, model.DefaultDataView);
        Assert.Equal(2, model.MaxParallelismPerQuery);
        Assert.Equal(1, model.MaxParallelismPerRefresh);
        Assert.Equal("en-US", model.SourceQueryCulture);
        Assert.True(model.ForceUniqueNames);
        Assert.Equal("described", model.Description);
    }

    [Fact]
    public void SetProperty_CompatibilityLevel_Applies()
    {
        var (mutator, database) = NewModel();

        mutator.SetProperty(Set("compatibilityLevel", "1704"));

        Assert.Equal(1704, database.CompatibilityLevel);
    }

    [Fact]
    public void SetProperty_EnumsAndBool_RejectUnknownNames()
    {
        var (mutator, _) = NewModel();

        var mode = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("defaultMode", "Bogus")));
        Assert.Contains("must be one of: Import, DirectQuery, Default, Push, Dual, DirectLake", mode.Message);

        var dataView = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("defaultDataView", "Bogus")));
        Assert.Contains("must be one of: Full, Sample, Default", dataView.Message);

        var flag = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("forceUniqueNames", "maybe")));
        Assert.Contains("must be true or false", flag.Message);
    }

    [Fact]
    public void SetProperty_ModelScalars_ReadBackFromSnapshot()
    {
        var (mutator, database) = NewModel();

        mutator.SetProperty(Set("culture", "en-US"));
        mutator.SetProperty(Set("defaultMode", "Dual"));
        mutator.SetProperty(Set("discourageImplicitMeasures", "true"));
        mutator.SetProperty(Set("maxParallelismPerQuery", "2"));
        mutator.SetProperty(Set("description", "described"));

        var snapshot = TomModelSummarizer.Snapshot(database, "M");

        Assert.Equal("en-US", snapshot.Properties![PropertyBagKeys.Culture]);
        Assert.Equal("Dual", snapshot.Properties![PropertyBagKeys.DefaultMode]);
        Assert.Equal("true", snapshot.Properties![PropertyBagKeys.DiscourageImplicitMeasures]);
        Assert.Equal("2", snapshot.Properties![PropertyBagKeys.MaxParallelismPerQuery]);
        Assert.Equal("described", snapshot.Description);
    }

    [Fact]
    public void SetProperty_DiscourageReportMeasures_IsRejectedAsUnsettable()
    {
        // TOM's setter only accepts this flag at the internal-only compatibility sentinel,
        // so set rejects it outright instead of letting the raw violation surface.
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("discourageReportMeasures", "true")));

        Assert.Contains("not supported for the model root", ex.Message);
        var hint = ex.Message[ex.Message.IndexOf("Writable properties:")..];
        Assert.DoesNotContain("discourageReportMeasures", hint);
    }

    [Fact]
    public void SetProperty_UnknownModelRootProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("compatibilityLevel", ex.Message);
        Assert.Contains("culture", ex.Message);
        Assert.Contains("forceUniqueNames", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new(".", [new ModelPropertyAssignment(property, value)], null);

    private static (TomModelMutator Mutator, Database Database) NewModel()
    {
        // DiscourageImplicitMeasures (1470+), SourceQueryCulture (1520+),
        // DiscourageCompositeModels (1560+), and the parallelism caps (1568+/1569+) are
        // all cleared by 1702.
        var database = NewDatabase(compatibilityLevel: 1702);
        return (new TomModelMutator(database), database);
    }
}
