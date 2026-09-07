using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Calculation-item property coverage for set: the writable scalars (description, expression,
/// ordinal) on a calculation-group table, value parsing, and read-back through the summarizer.
/// </summary>
public sealed class TomCalculationItemPropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, item) = NewModel();

        mutator.SetProperty(Set("description", "current quarter"));
        mutator.SetProperty(Set("expression", "[M] * 3"));
        // The rename goes last: it changes the path the other assignments resolve through.
        mutator.SetProperty(Set("name", "CI2"));

        Assert.Equal("current quarter", item.Description);
        Assert.Equal("[M] * 3", item.Expression);
        Assert.Equal("CI2", item.Name);
    }

    [Fact]
    public void SetProperty_Ordinal_ParsesAndRejects()
    {
        var (mutator, item) = NewModel();

        mutator.SetProperty(Set("ordinal", "2"));

        Assert.Equal(2, item.Ordinal);

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("ordinal", "second")));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, item) = NewModel();

        mutator.SetProperty(Set("ordinal", "2"));
        mutator.SetProperty(Set("description", "current quarter"));

        var snapshot = TomModelSummarizer.Snapshot((Database)item.Model.Database, "M");
        var calcItem = snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Table && o.Name == "CG")
            .Children.Single(c => c.Kind == ModelObjectKind.CalculationItem);
        var projected = ModelPropertyCatalog.Project(calcItem);

        Assert.Equal(2, projected["ordinal"]);
        Assert.Equal("current quarter", projected["description"]);
    }

    [Fact]
    public void SetProperty_UnknownCalculationItemProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("ordinal", ex.Message);
        Assert.Contains("expression", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("CG/CI", [new ModelPropertyAssignment(property, value)], ModelObjectKind.CalculationItem);

    private static (TomModelMutator Mutator, CalculationItem Item) NewModel()
    {
        var db = NewDatabase(compatibilityLevel: 1702);
        var calcGroupTable = new Table { Name = "CG" };
        calcGroupTable.CalculationGroup = new CalculationGroup { Precedence = 1 };
        var item = new CalculationItem { Name = "CI", Expression = "[M] * 2" };
        calcGroupTable.CalculationGroup.CalculationItems.Add(item);
        db.Model.Tables.Add(calcGroupTable);
        return (new TomModelMutator(db), item);
    }
}
