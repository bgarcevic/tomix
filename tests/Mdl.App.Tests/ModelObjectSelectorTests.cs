using Mdl.Core.Models;
using Mdl.Core.Paths;

namespace Mdl.App.Tests;

public sealed class ModelObjectSelectorTests
{
    // A small model: two real tables plus a table literally named "Measures" (to test quoting),
    // a couple of roles, a relationship, a perspective and a culture.
    private static ModelSnapshot Snapshot()
    {
        var sales = Node("Sales", ModelObjectKind.Table,
            Node("SaleID", ModelObjectKind.Column),
            Node("Amount", ModelObjectKind.Column),
            Node("Total Sales", ModelObjectKind.Measure),
            Node("Geography", ModelObjectKind.Hierarchy,
                Node("Country", ModelObjectKind.Level),
                Node("City", ModelObjectKind.Level)),
            Node("Sales", ModelObjectKind.Partition));

        var customers = Node("Customers", ModelObjectKind.Table,
            Node("CustomerID", ModelObjectKind.Column),
            Node("Amount", ModelObjectKind.Measure));

        var literalMeasures = Node("Measures", ModelObjectKind.Table,
            Node("Dummy", ModelObjectKind.Column));

        var readers = Node("Admins", ModelObjectKind.Role,
            Node("alice@contoso.com", ModelObjectKind.RoleMember));
        var region = Node("Region", ModelObjectKind.Role,
            Node("bob@contoso.com", ModelObjectKind.RoleMember));

        var relationship = Node("rel-1", ModelObjectKind.Relationship);
        var perspective = Node("Sales View", ModelObjectKind.Perspective);
        var culture = Node("en-US", ModelObjectKind.Culture);

        return new ModelSnapshot("test", 1601,
            [sales, customers, literalMeasures, readers, region, relationship, perspective, culture]);
    }

    private static ModelObject Node(string name, ModelObjectKind kind, params ModelObject[] children)
        => new(name, kind, name, Detail: null, Expression: null, Description: null, Hidden: false, Children: children);

    private static string[] Names(string? path, ModelObjectKind? type = null)
        => ModelObjectSelector.Select(Snapshot(), path, type).Select(o => o.Name).ToArray();

    [Fact]
    public void EmptyPath_ListsTables()
        => Assert.Equal(["Sales", "Customers", "Measures"], Names(""));

    [Fact]
    public void TablesKeyword_ListsTables()
        => Assert.Equal(["Sales", "Customers", "Measures"], Names("Tables"));

    [Fact]
    public void MeasuresKeyword_ListsAllMeasuresAcrossTables()
        => Assert.Equal(["Total Sales", "Amount"], Names("Measures"));

    [Fact]
    public void ExactTableName_ExpandsToChildren()
        => Assert.Equal(["SaleID", "Amount", "Total Sales", "Geography", "Sales"], Names("Sales"));

    [Fact]
    public void WildcardTable_ListsTablesNotChildren()
        => Assert.Equal(["Sales"], Names("Sa*"));

    [Fact]
    public void WildcardTable_IsCaseInsensitive()
        => Assert.Equal(["Sales"], Names("sa*"));

    [Fact]
    public void TableThenMeasuresKeyword_ListsThatTablesMeasures()
        => Assert.Equal(["Total Sales"], Names("Sales/Measures"));

    [Fact]
    public void TableThenWildcard_DescendsIntoChildren()
        => Assert.Equal(["Amount"], Names("Sales/*Amount"));

    [Fact]
    public void WildcardThenName_FindsObjectAcrossEveryTable()
        => Assert.Equal(["Amount", "Amount"], Names("*/Amount"));

    [Fact]
    public void RolesKeywordThenWildcardThenMembers_FiltersRolesThenListsMembers()
        => Assert.Equal(["bob@contoso.com"], Names("Roles/Re*/Members"));

    [Fact]
    public void HierarchyThenLevelsKeyword_ListsLevels()
        => Assert.Equal(["Country", "City"], Names("Sales/Geography/Levels"));

    [Fact]
    public void Quoting_ForcesLiteralNameOverKeyword()
        => Assert.Equal(["Dummy"], Names("'Measures'")); // the table named "Measures", expanded

    [Fact]
    public void TypeFilter_WithNoPath_ListsAllOfThatKind()
        => Assert.Equal(["Total Sales", "Amount"], Names(null, ModelObjectKind.Measure));

    [Fact]
    public void TypeFilter_NarrowsAPathResult()
        => Assert.Equal(["Total Sales"], Names("Sales", ModelObjectKind.Measure));

    [Fact]
    public void Keywords_ResolveModelLevelCollections()
    {
        Assert.Equal(["Admins", "Region"], Names("Roles"));
        Assert.Equal(["rel-1"], Names("Relationships"));
        Assert.Equal(["Sales View"], Names("Perspectives"));
        Assert.Equal(["en-US"], Names("Cultures"));
    }
}
