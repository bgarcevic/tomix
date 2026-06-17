using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Exercises the actual bundled rule expressions (loaded from bpa-rules.json) against synthetic
/// snapshots, to prove the previously-dark rules now compile and produce correct results after the
/// adapter/snapshot enrichment.
/// </summary>
public sealed class BpaRuleCoverageTests
{
    private static BpaRule BundledRule(string id)
        => BpaRuleLoader.LoadDefaultRules().Single(r => r.Id == id);

    private static string[] Flagged(BpaRule rule, ModelSnapshot snapshot)
        => new BpaEngine()
            .Evaluate(snapshot, new BpaEngineOptions([rule]))
            .Violations
            .Select(v => v.ObjectPath)
            .OrderBy(p => p)
            .ToArray();

    [Fact]
    public void RemoveRolesWithNoMembers_FlagsEmptyRolesOnly()
    {
        var empty = Role("Empty", members: []);
        var staffed = Role("Staffed", members: ["user@contoso.com"]);
        var snapshot = new ModelSnapshot("M", 1601, [Table("T"), empty, staffed]);

        Assert.Equal(["Roles/Empty"], Flagged(BundledRule("REMOVE_ROLES_WITH_NO_MEMBERS"), snapshot));
    }

    [Fact]
    public void RelationshipColumnsSameDataType_FlagsTypeMismatch()
    {
        var a = Column("a", "A", dataType: "Int64");
        var b = Column("b", "B", dataType: "String");
        var tableA = Table("A", a);
        var tableB = Table("B", b);
        var rel = Relationship("A_B", "A", "a", "B", "b");
        var snapshot = new ModelSnapshot("M", 1601, [tableA, tableB, rel]);

        Assert.Equal(["Relationships/A_B"], Flagged(BundledRule("RELATIONSHIP_COLUMNS_SAME_DATA_TYPE"), snapshot));
    }

    [Fact]
    public void UnnecessaryColumns_FlagsHiddenUnreferenced_ButNotHierarchyColumns()
    {
        var orphan = Column("orphan", "T", hidden: true);
        var inHierarchy = Column("level", "T", hidden: true, usedInHierarchies: "Geography");
        var snapshot = new ModelSnapshot("M", 1601, [Table("T", orphan, inHierarchy)]);

        Assert.Equal(["T/orphan"], Flagged(BundledRule("UNNECESSARY_COLUMNS"), snapshot));
    }

    [Fact]
    public void CalculationGroupsWithNoCalculationItems_FlagsEmptyGroup()
    {
        var empty = CalculationGroupTable("TimeIntelligence");
        var populated = CalculationGroupTable("Currency", CalculationItem("Item", "T", "SELECTEDMEASURE()"));
        var snapshot = new ModelSnapshot("M", 1601, [empty, populated]);

        Assert.Equal(["TimeIntelligence"], Flagged(BundledRule("CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS"), snapshot));
    }

    [Theory]
    [InlineData("Name == current.Name")]                       // top-level current only
    [InlineData("not IsHidden and Name == current.Name")]      // implicit it + current
    [InlineData("not UsedInSortBy.Any(Name == current.Name)")] // nested Any + current
    public void Diag_CurrentKeyword(string expression)
    {
        var snapshot = new ModelSnapshot("M", 1601, [Table("T", Column("c", "T"))]);
        var rule = new BpaRule("R", "r", "c", BpaSeverity.Info, ["DataColumn"], Expression: expression);
        Assert.Equal(["T/c"], Flagged(rule, snapshot));
    }

    // --- snapshot builders -------------------------------------------------

    private static ModelObject Table(string name, params ModelObject[] children)
        => new(name, ModelObjectKind.Table, name,
            Detail: null, Expression: null, Description: "desc", Hidden: false, SourceColumn: null,
            Children: children,
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table", ["TableObjectType"] = "Table" });

    private static ModelObject CalculationGroupTable(string name, params ModelObject[] items)
        => new(name, ModelObjectKind.Table, name,
            Detail: null, Expression: null, Description: "desc", Hidden: false, SourceColumn: null,
            Children: items,
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table", ["TableObjectType"] = "CalculationGroup" });

    private static ModelObject CalculationItem(string name, string table, string expression)
        => new(name, ModelObjectKind.CalculationItem, $"{table}/{name}",
            Detail: null, Expression: expression, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "CalculationItem" });

    private static ModelObject Column(
        string name, string table, string dataType = "Int64", bool hidden = false, string? usedInHierarchies = null)
    {
        var props = new Dictionary<string, string>
        {
            ["DataType"] = dataType,
            ["ObjectType"] = "DataColumn",
            ["SourceColumn"] = name,
        };
        if (usedInHierarchies is not null)
            props["UsedInHierarchies"] = usedInHierarchies;

        return new ModelObject(name, ModelObjectKind.Column, $"{table}/{name}",
            Detail: null, Expression: null, Description: "desc", Hidden: hidden, SourceColumn: name,
            Children: [], Properties: props);
    }

    private static ModelObject Role(string name, string[] members)
    {
        var children = members
            .Select(m => new ModelObject(m, ModelObjectKind.RoleMember, $"Roles/{name}/{m}",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []))
            .ToArray();

        return new ModelObject(name, ModelObjectKind.Role, $"Roles/{name}",
            Detail: null, Expression: null, Description: "desc", Hidden: false, SourceColumn: null,
            Children: children,
            Properties: new Dictionary<string, string> { ["ObjectType"] = "ModelRole", ["RlsExpression"] = "" });
    }

    private static ModelObject Relationship(string name, string fromTable, string fromCol, string toTable, string toCol)
        => new(name, ModelObjectKind.Relationship, $"Relationships/{name}",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string>
            {
                ["FromTable"] = fromTable,
                ["ToTable"] = toTable,
                ["FromColumn"] = $"{fromTable}[{fromCol}]",
                ["ToColumn"] = $"{toTable}[{toCol}]",
                ["FromCardinality"] = "Many",
                ["ToCardinality"] = "One",
                ["CrossFilteringBehavior"] = "OneDirection",
                ["IsActive"] = "true",
                ["ObjectType"] = "Relationship",
            });
}
