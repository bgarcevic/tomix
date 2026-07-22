using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <c>tx replace</c> rewrites every expression-bearing property TE-CLI documents for its
/// expression walk (KPIs, detail rows, format-string definitions, refresh-policy M, calculation
/// groups, table permissions) plus tomix's shared expressions and functions — applied, not just
/// previewed.
/// </summary>
public sealed class TomReplaceCoverageTests
{
    [Fact]
    public void ExpressionsScope_RewritesEveryExpressionBearingProperty()
    {
        var db = CatalogSearchableAgreementTests.KitchenSink();
        db.Model.Functions.Add(new Function
        {
            Name = "driftHelper",
            Expression = "FUNCTION() => COUNTROWS(driftSales)",
            Description = "drift function description"
        });
        var mutator = new TomModelMutator(db);

        mutator.ReplaceText(Replace("drift", "lifted", "expressions"));

        var sales = db.Model.Tables["driftSales"];
        var measure = sales.Measures["driftTotal"];
        Assert.Equal("SUM(liftedSales[liftedAmount])", measure.Expression);
        Assert.Equal("TOPN(10, liftedSales)", measure.DetailRowsDefinition.Expression);
        Assert.Equal("\"lifted-fmt\"", measure.FormatStringDefinition.Expression);
        Assert.Equal("[liftedTotal] * 1.1", measure.KPI.TargetExpression);
        Assert.Contains("lifted", measure.KPI.StatusExpression);
        Assert.Contains("lifted", measure.KPI.TrendExpression);
        Assert.Equal("SELECTCOLUMNS(liftedSales)", sales.DefaultDetailRowsDefinition.Expression);

        var policy = Assert.IsType<BasicRefreshPolicy>(sales.RefreshPolicy);
        Assert.Equal("let Source = liftedSource in Source", policy.SourceExpression);
        Assert.Equal("let Poll = liftedPoll in Poll", policy.PollingExpression);

        Assert.Equal("COUNTROWS(liftedSales)", ((CalculatedColumn)sales.Columns["driftCalc"]).Expression);
        Assert.Equal(
            "let Source = liftedQuery in Source",
            ((MPartitionSource)sales.Partitions["driftPartition"].Source).Expression);
        Assert.Equal(
            "FILTER(liftedSales, TRUE())",
            ((CalculatedPartitionSource)db.Model.Tables["driftCalcTable"].Partitions["driftCalcTable"].Source).Expression);

        var group = db.Model.Tables["driftTimeCalcs"].CalculationGroup;
        Assert.Equal("SELECTEDMEASURE() -- liftedNone", group.NoSelectionExpression.Expression);
        Assert.Equal("SELECTEDMEASURE() -- liftedMulti", group.MultipleOrEmptySelectionExpression.Expression);
        var item = group.CalculationItems["driftYtd"];
        Assert.Equal("CALCULATE(SELECTEDMEASURE(), DATESYTD(liftedDates)) ", item.Expression);
        Assert.Equal("\"lifted-item-fmt\"", item.FormatStringDefinition.Expression);

        Assert.Equal(
            "liftedSales[liftedAmount] > 0",
            db.Model.Roles["driftRole"].TablePermissions[0].FilterExpression);
        Assert.Equal(
            "\"liftedValue\" meta [IsParameterQuery=true]",
            db.Model.Expressions["driftParameter"].Expression);
        Assert.Equal("FUNCTION() => COUNTROWS(liftedSales)", db.Model.Functions["driftHelper"].Expression);
    }

    [Fact]
    public void NamesScope_RewritesNamesOfEveryAddressableKind()
    {
        var db = CatalogSearchableAgreementTests.KitchenSink();
        var mutator = new TomModelMutator(db);

        mutator.ReplaceText(Replace("drift", "lifted", "names"));

        var sales = db.Model.Tables["liftedSales"];
        Assert.NotNull(sales);
        Assert.Equal("liftedTotal", sales.Measures[0].Name);
        Assert.Equal("liftedAmount", sales.Columns["liftedAmount"].Name);
        Assert.Equal("liftedHierarchy", sales.Hierarchies[0].Name);
        Assert.Equal("liftedLevel", sales.Hierarchies[0].Levels[0].Name);
        Assert.Equal("liftedPartition", sales.Partitions[0].Name);
        Assert.Equal("liftedYtd", db.Model.Tables["liftedTimeCalcs"].CalculationGroup.CalculationItems[0].Name);
        Assert.Equal("liftedRole", db.Model.Roles[0].Name);
        // TOM freezes MemberName, so this rename replaces the member; id and annotations survive.
        var member = Assert.Single(db.Model.Roles[0].Members);
        Assert.Equal("lifted@example.com", member.MemberName);
        Assert.Equal("S-1-5-21-42", member.MemberID);
        Assert.Equal("drift member annotation", member.Annotations["MemberTag"].Value);
        Assert.Equal("liftedPerspective", db.Model.Perspectives[0].Name);
        Assert.Equal("liftedWarehouse", db.Model.DataSources[0].Name);
        Assert.Equal("liftedParameter", db.Model.Expressions[0].Name);
    }

    [Fact]
    public void DescriptionsScope_IncludesModelKpiPartitionAndLevelDescriptions()
    {
        var db = CatalogSearchableAgreementTests.KitchenSink();
        var mutator = new TomModelMutator(db);

        var result = mutator.ReplaceText(Replace("drift", "lifted", "descriptions"));

        Assert.Equal("lifted model description", db.Model.Description);
        var sales = db.Model.Tables["driftSales"];
        Assert.Equal("lifted kpi description", sales.Measures[0].KPI.Description);
        Assert.Equal("lifted partition description", sales.Partitions[0].Description);
        Assert.Equal("lifted level description", sales.Hierarchies[0].Levels[0].Description);
        Assert.Contains(result.Previews, p => p.ObjectPath == "." && p.Property == "Description");
    }

    [Fact]
    public void PartitionPreviewPaths_MatchSnapshotPaths()
    {
        var db = CatalogSearchableAgreementTests.KitchenSink();
        var mutator = new TomModelMutator(db);

        var result = mutator.ReplaceText(Replace("driftQuery", "liftedQuery", "expressions", apply: false));

        var preview = Assert.Single(result.Previews);
        Assert.Equal("driftSales/driftPartition", preview.ObjectPath);
    }

    private static ModelReplaceRequest Replace(string pattern, string replacement, string scope, bool apply = true)
        => new(pattern, replacement, scope, Regex: false, CaseSensitive: false, Apply: apply);
}
