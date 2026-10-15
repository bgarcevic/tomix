using Tomix.App.Deps;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Tests;

public sealed class DependencyGraphTests
{
    // DAX resolves a lone [Col] against the expression's own table, so a same-named column in
    // another table is not what it reads.
    [Theory]
    [InlineData("Calculations/Revenue", "Calculations/FactoredProductRevenue", "Sales/FactoredProductRevenue")]
    [InlineData("DateTableTemplate/Year", "DateTableTemplate/Date", "Sales/Date")]
    [InlineData("DateTableTemplate", "DateTableTemplate/Date", "Sales/Date")]
    public void A_lone_column_resolves_to_its_own_table(string source, string own, string other)
    {
        var graph = new DependencyGraph(Model());

        Assert.Contains(graph.SitesReferencing(Find(own)), s => s.Source.Path == source);
        Assert.DoesNotContain(graph.SitesReferencing(Find(other)), s => s.Source.Path == source);
        Assert.Contains(own, Upstream(graph, source));
        Assert.DoesNotContain(other, Upstream(graph, source));
    }

    [Fact]
    public void A_lone_column_its_table_lacks_still_counts_for_every_table_that_has_it()
    {
        // [Region] names no column of Calculations, so which table it means is unknown: every
        // candidate counts, so a removal blocks rather than breaking a reference.
        var graph = new DependencyGraph(Model());

        Assert.Contains(graph.SitesReferencing(Find("Sales/Region")), s => s.Source.Path == "Calculations/ByRegion");
        Assert.Contains(graph.SitesReferencing(Find("Geo/Region")), s => s.Source.Path == "Calculations/ByRegion");
    }

    [Fact]
    public void A_role_filter_resolves_lone_columns_to_the_table_it_filters()
    {
        var graph = new DependencyGraph(Model());

        Assert.Contains(graph.SitesReferencing(Find("Geo/Region")), s => s.Source.Path == "Roles/Reader/Geo");
        Assert.DoesNotContain(graph.SitesReferencing(Find("Sales/Region")), s => s.Source.Path == "Roles/Reader/Geo");
    }

    [Fact]
    public void A_lone_measure_still_resolves_model_wide()
    {
        var graph = new DependencyGraph(Model());

        Assert.Contains("Sales/Total", Upstream(graph, "Calculations/UsesTotal"));
    }

    private static readonly IReadOnlyList<ModelObject> Objects = Build();

    private static IReadOnlyList<ModelObject> Model() => Objects;

    private static ModelObject Find(string path) => Objects.Single(o => o.Path == path);

    private static List<string> Upstream(DependencyGraph graph, string path)
        => graph.Deep(Find(path), upstream: true, maxDepth: 1).Select(d => d.Path).ToList();

    private static IReadOnlyList<ModelObject> Build()
    {
        var sales = Table("Sales",
            Column("Sales", "FactoredProductRevenue"),
            Column("Sales", "Date"),
            Column("Sales", "Region"),
            Measure("Sales", "Total", "SUM(Sales[FactoredProductRevenue])"));
        var geo = Table("Geo", Column("Geo", "Region"));
        var calculations = Table("Calculations",
            Column("Calculations", "FactoredProductRevenue"),
            Measure("Calculations", "Revenue", "SUM([FactoredProductRevenue])"),
            Measure("Calculations", "ByRegion", "DISTINCTCOUNT([Region])"),
            Measure("Calculations", "UsesTotal", "[Total] * 2"));
        var template = Table("DateTableTemplate",
            Column("DateTableTemplate", "Date"),
            Column("DateTableTemplate", "Year", "YEAR([Date])")) with
        {
            Properties = new Dictionary<string, string> { [PropertyBagKeys.DefaultDetailRowsExpression] = "SELECTCOLUMNS(DateTableTemplate, \"d\", [Date])" }
        };
        var permission = new ModelObject("Geo", ModelObjectKind.TablePermission, "Roles/Reader/Geo", null,
            "[Region] = \"EU\"", null, false, null, []);
        var role = new ModelObject("Reader", ModelObjectKind.Role, "Roles/Reader", null, null, null, false, null, [permission]);

        var objects = new List<ModelObject>();
        void Walk(ModelObject o)
        {
            objects.Add(o);
            foreach (var child in o.Children)
                Walk(child);
        }

        foreach (var root in new[] { sales, geo, calculations, template, role })
            Walk(root);
        return objects;
    }

    private static ModelObject Table(string name, params ModelObject[] children)
        => new(name, ModelObjectKind.Table, name, null, null, null, false, null, children);

    private static ModelObject Column(string table, string name, string? expression = null)
        => new(name, expression is null ? ModelObjectKind.Column : ModelObjectKind.CalculatedColumn,
            $"{table}/{name}", null, expression, null, false, null, []);

    private static ModelObject Measure(string table, string name, string expression)
        => new(name, ModelObjectKind.Measure, $"{table}/{name}", null, expression, null, false, null, []);
}
