using Microsoft.AnalysisServices.Tabular;
using Tomix.App.Bpa.Model;
using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Validate;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.App.Tests;

/// <summary>
/// The DAX sites the commands scan, read from a real TOM model through the summarizer: every
/// DAX-bearing TOM property surfaces as exactly one site, each site's rewrite lands back on that
/// property, and RLS filters (one per table permission, spanning several lines) validate and
/// reach BPA whole.
/// </summary>
public sealed class DaxSitesTomParityTests
{
    private static readonly (string Path, string Property)[] ExpectedSites =
    [
        ("Sales/Total", "Expression"),
        ("Sales/Total", "FormatStringExpression"),
        ("Sales/Total", "DetailRowsExpression"),
        ("Sales/Total", "KpiTargetExpression"),
        ("Sales/Total", "KpiStatusExpression"),
        ("Sales/Total", "KpiTrendExpression"),
        ("Sales/Double", "Expression"),
        ("Sales", "DefaultDetailRowsExpression"),
        ("Scenario", "NoSelectionExpression"),
        ("Scenario", "MultipleOrEmptySelectionExpression"),
        ("Scenario/Current", "Expression"),
        ("Scenario/Current", "FormatStringExpression"),
        ("Roles/Reader/Sales", "FilterExpression"),
        ("Roles/Reader/Region", "FilterExpression"),
    ];

    [Fact]
    public void Sites_CoverEveryDaxPropertyOfTheTomModel()
    {
        var sites = SitesOf(TomModelSummarizer.Snapshot(NewModel(), "m"))
            .Select(s => (s.Path, s.Site.Property))
            .ToList();

        Assert.Equal(
            ExpectedSites.OrderBy(s => s.Path).ThenBy(s => s.Property),
            sites.OrderBy(s => s.Path).ThenBy(s => s.Property));
    }

    [Fact]
    public void RewriteExpressions_LandsEverySiteOnItsOwnProperty()
    {
        var db = NewModel();
        var before = SitesOf(TomModelSummarizer.Snapshot(db, "m")).ToList();

        new TomModelMutator(db).RewriteExpressions(before
            .Select(s => new ModelExpressionEdit(s.Path, s.Kind, s.Site.Property, $"{s.Site.Expression} /* edited */"))
            .ToList());

        var after = SitesOf(TomModelSummarizer.Snapshot(db, "m"))
            .ToDictionary(s => (s.Path, s.Site.Property), s => s.Site.Expression);
        foreach (var site in before)
            Assert.Equal($"{site.Site.Expression} /* edited */", after[(site.Path, site.Site.Property)]);
    }

    [Fact]
    public async Task Validate_AcceptsMultiLineRlsFilters()
    {
        var result = await new ValidateModelHandler([new MutationStubs.Provider(new MutationStubs.SnapshotSession(TomModelSummarizer.Snapshot(NewModel(), "m")))])
            .HandleAsync(
                new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
                CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Errors);
    }

    [Fact]
    public void Bpa_KeepsEachRlsFilterWhole()
    {
        var model = BpaModelBuilder.Build(TomModelSummarizer.Snapshot(NewModel(), "m"));

        var role = Assert.Single(model.Roles);
        Assert.Equal([SalesFilter, RegionFilter], role.RowLevelSecurity);
        Assert.Equal([SalesFilter], model.Tables.Single(t => t.Name == "Sales").RowLevelSecurity);
    }

    private const string SalesFilter = "CONTAINS (\n    Sales,\n    Sales[Region], USERPRINCIPALNAME ()\n)";
    private const string RegionFilter = "NOT ISBLANK (\n    Region[Code]\n)";

    private static IEnumerable<(string Path, ModelObjectKind Kind, DaxSite Site)> SitesOf(ModelSnapshot snapshot)
        => ModelObjectProjection.Flatten(snapshot)
            .SelectMany(o => DaxExpressions.Sites(o).Select(site => (o.Path, o.Kind, site)));

    /// <summary>One of each DAX-bearing TOM property, with generic names.</summary>
    private static Database NewModel()
    {
        var db = new Database { Name = "M", ID = "M", CompatibilityLevel = 1702, Model = new Model { Name = "Model" } };

        var sales = new Table { Name = "Sales" };
        sales.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "let x = 1 in x" } });
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal, SourceColumn = "Amount" });
        sales.Columns.Add(new DataColumn { Name = "Region", DataType = DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new CalculatedColumn { Name = "Double", DataType = DataType.Decimal, Expression = "Sales[Amount] * 2" });
        sales.DefaultDetailRowsDefinition = new DetailRowsDefinition { Expression = "SELECTCOLUMNS ( Sales, \"A\", Sales[Amount] )" };
        sales.Measures.Add(new Measure
        {
            Name = "Total",
            Expression = "SUM ( Sales[Amount] )",
            FormatStringDefinition = new FormatStringDefinition { Expression = "\"#,0\"" },
            DetailRowsDefinition = new DetailRowsDefinition { Expression = "Sales" },
            KPI = new KPI
            {
                TargetExpression = "100",
                StatusExpression = "IF ( [Total] > 100, 1, -1 )",
                TrendExpression = "0"
            }
        });
        db.Model.Tables.Add(sales);

        var region = new Table { Name = "Region" };
        region.Partitions.Add(new Partition { Name = "Region", Source = new MPartitionSource { Expression = "let x = 1 in x" } });
        region.Columns.Add(new DataColumn { Name = "Code", DataType = DataType.String, SourceColumn = "Code" });
        db.Model.Tables.Add(region);

        var scenario = new Table
        {
            Name = "Scenario",
            CalculationGroup = new CalculationGroup
            {
                NoSelectionExpression = new CalculationGroupExpression { Expression = "SELECTEDMEASURE ()" },
                MultipleOrEmptySelectionExpression = new CalculationGroupExpression { Expression = "BLANK ()" }
            }
        };
        scenario.Partitions.Add(new Partition { Name = "Scenario", Source = new CalculationGroupSource() });
        scenario.Columns.Add(new DataColumn { Name = "Name", DataType = DataType.String, SourceColumn = "Name" });
        scenario.CalculationGroup.CalculationItems.Add(new CalculationItem
        {
            Name = "Current",
            Expression = "SELECTEDMEASURE ()",
            FormatStringDefinition = new FormatStringDefinition { Expression = "SELECTEDMEASUREFORMATSTRING ()" }
        });
        db.Model.Tables.Add(scenario);

        var role = new ModelRole { Name = "Reader", ModelPermission = ModelPermission.Read };
        role.TablePermissions.Add(new TablePermission { Table = sales, FilterExpression = SalesFilter });
        role.TablePermissions.Add(new TablePermission { Table = region, FilterExpression = RegionFilter });
        db.Model.Roles.Add(role);

        return db;
    }
}
