using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Regression tests for mutation-path resolution, from live-model QA findings: DAX bracket paths
/// must never hit partitions, same-name collisions must be ambiguous instead of silently picking
/// one, names with embedded apostrophes must resolve, and object types with property handlers
/// (relationships, expressions, calculation items, cultures, levels, members) must be reachable.
/// </summary>
public sealed class TomSetResolutionTests
{
    [Fact]
    public void SetProperty_DaxForm_TargetsMeasure_NotSameNamedPartition()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Budget");
        table.Measures.Add(new Measure { Name = "Budget", Expression = "1" });
        // AddTable already created a partition named "Budget" (the Desktop convention).

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "'Budget'[Budget]", [new ModelPropertyAssignment("expression", "2")], null));

        Assert.Equal("2", table.Measures["Budget"].Expression);
        Assert.Equal("let x = 1 in x", ((MPartitionSource)table.Partitions["Budget"].Source).Expression);
    }

    [Fact]
    public void SetProperty_DaxForm_NeverResolvesPartition()
    {
        var db = NewDatabase();
        AddTable(db, "Budget"); // partition "Budget" exists, no measure/column of that name

        var mutator = new TomModelMutator(db);
        Assert.Throws<ObjectNotFoundException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "'Budget'[Budget]", [new ModelPropertyAssignment("expression", "2")], null)));
    }

    [Fact]
    public void SetProperty_SlashForm_SameNamedMeasureAndPartition_IsAmbiguous()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Budget");
        table.Measures.Add(new Measure { Name = "Budget", Expression = "1" });

        var mutator = new TomModelMutator(db);
        var ex = Assert.Throws<AmbiguousObjectException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "Budget/Budget", [new ModelPropertyAssignment("name", "X")], null)));
        Assert.Contains("--type", ex.Message);
    }

    [Fact]
    public void SetProperty_TypeOption_DisambiguatesToPartition()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Budget");
        table.Measures.Add(new Measure { Name = "Budget", Expression = "1" });

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "Budget/Budget", [new ModelPropertyAssignment("name", "Renamed")], ModelObjectKind.Partition));

        Assert.Equal("Renamed", table.Partitions.First().Name);
        Assert.Equal("Budget", table.Measures.First().Name);
    }

    [Fact]
    public void SetProperty_PartitionsKeywordPath_DisambiguatesToPartition()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Budget");
        table.Measures.Add(new Measure { Name = "Budget", Expression = "1" });

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "tables/Budget/partitions/Budget", [new ModelPropertyAssignment("name", "Renamed")], null));

        Assert.Equal("Renamed", table.Partitions.First().Name);
    }

    [Fact]
    public void SetProperty_KeywordPath_ResolvesColumn()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Customers");
        table.Columns.Add(new DataColumn { Name = "E-mail", DataType = DataType.String });

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "tables/Customers/columns/E-mail", [new ModelPropertyAssignment("ishidden", "true")], null));

        Assert.True(table.Columns["E-mail"].IsHidden);
    }

    [Theory]
    [InlineData("'Månedens KPI''er'")]
    [InlineData("Månedens KPI'er")]
    public void SetProperty_TableNameWithApostrophe_Resolves(string path)
    {
        var db = NewDatabase();
        var table = AddTable(db, "Månedens KPI'er");

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            path, [new ModelPropertyAssignment("description", "desc")], null));

        Assert.Equal("desc", table.Description);
    }

    [Fact]
    public void SetProperty_DaxFormWithEscapedApostrophe_ResolvesMeasure()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Månedens KPI'er");
        table.Measures.Add(new Measure { Name = "# Måltal", Expression = "1" });

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "'Månedens KPI''er'[# Måltal]", [new ModelPropertyAssignment("description", "d")], null));

        Assert.Equal("d", table.Measures.First().Description);
    }

    [Fact]
    public void SetProperty_RelationshipByEndpoints_SetsIsActive()
    {
        var db = NewDatabase();
        var relationship = AddRelationship(db);
        relationship.IsActive = false;

        var mutator = new TomModelMutator(db);
        var result = mutator.SetProperty(new ModelObjectSetRequest(
            "'Sales'[Key]->'Product'[Key]", [new ModelPropertyAssignment("isactive", "true")], null));

        Assert.True(relationship.IsActive);
        Assert.Equal("Sales[Key] -> Product[Key]", result.Path);
    }

    [Fact]
    public void SetProperty_RelationshipByName_SetsCrossFilteringBehavior()
    {
        var db = NewDatabase();
        var relationship = AddRelationship(db);

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            relationship.Name, [new ModelPropertyAssignment("crossfilteringbehavior", "BothDirections")], null));

        Assert.Equal(CrossFilteringBehavior.BothDirections, relationship.CrossFilteringBehavior);
    }

    [Fact]
    public void SetProperty_NamedExpression_SetsExpression()
    {
        var db = NewDatabase();
        var expression = new NamedExpression { Name = "DatabaseSchema", Kind = ExpressionKind.M, Expression = "\"analytics\"" };
        db.Model.Expressions.Add(expression);

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "expressions/DatabaseSchema", [new ModelPropertyAssignment("expression", "\"qa\"")], null));

        Assert.Equal("\"qa\"", expression.Expression);
    }

    [Fact]
    public void SetProperty_CalculationItem_SetsExpression()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Time Intelligence");
        table.CalculationGroup = new CalculationGroup();
        var item = new CalculationItem { Name = "CY", Expression = "SELECTEDMEASURE()" };
        table.CalculationGroup.CalculationItems.Add(item);

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "Time Intelligence/CY", [new ModelPropertyAssignment("expression", "SELECTEDMEASURE() + 0")], null));

        Assert.Equal("SELECTEDMEASURE() + 0", item.Expression);
    }

    [Fact]
    public void SetProperty_Culture_Renames()
    {
        var db = NewDatabase();
        var culture = new Culture { Name = "da-DK" };
        db.Model.Cultures.Add(culture);

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "cultures/da-DK", [new ModelPropertyAssignment("name", "en-US")], null));

        Assert.Equal("en-US", culture.Name);
    }

    [Fact]
    public void SetProperty_Level_ResolvesThreePartPath()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Dates");
        var column = new DataColumn { Name = "Year", DataType = DataType.Int64 };
        table.Columns.Add(column);
        var hierarchy = new Hierarchy { Name = "Calendar" };
        hierarchy.Levels.Add(new Level { Name = "Year", Column = column });
        table.Hierarchies.Add(hierarchy);

        var mutator = new TomModelMutator(db);
        mutator.SetProperty(new ModelObjectSetRequest(
            "Dates/Calendar/Year", [new ModelPropertyAssignment("description", "level desc")], null));

        Assert.Equal("level desc", hierarchy.Levels.First().Description);
    }

    [Fact]
    public void SetProperty_RoleMember_IsReachable_AndSurfacesTomImmutability()
    {
        // TOM makes MemberName immutable once set; the point here is that the member RESOLVES
        // (previously "Object not found") so TOM's own clear error reaches the user.
        var db = NewDatabase();
        var role = new ModelRole { Name = "Readers" };
        role.Members.Add(new ExternalModelRoleMember { MemberName = "user@contoso.com", IdentityProvider = "AzureAD" });
        db.Model.Roles.Add(role);

        var mutator = new TomModelMutator(db);
        var ex = Assert.Throws<InvalidOperationException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "Readers/user@contoso.com", [new ModelPropertyAssignment("membername", "other@contoso.com")], null)));
        Assert.Contains("immutable", ex.Message);
    }

    [Fact]
    public void SetProperty_NotFound_ThrowsObjectNotFoundWithHint()
    {
        var mutator = new TomModelMutator(NewDatabase());

        var ex = Assert.Throws<ObjectNotFoundException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "NoSuch", [new ModelPropertyAssignment("description", "x")], null)));
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void RemoveObject_SameNamedMeasureAndPartition_IsAmbiguous()
    {
        var db = NewDatabase();
        var table = AddTable(db, "Budget");
        table.Measures.Add(new Measure { Name = "Budget", Expression = "1" });

        var mutator = new TomModelMutator(db);
        Assert.Throws<AmbiguousObjectException>(() => mutator.RemoveObject(
            new ModelObjectRemoveRequest("Budget/Budget", null, IfExists: false)));
    }

    [Fact]
    public void SetProperty_UnsupportedPropertyError_NamesResolvedType()
    {
        var db = NewDatabase();
        AddTable(db, "Budget"); // only the partition named "Budget" matches Budget/Budget

        var mutator = new TomModelMutator(db);
        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "Budget/Budget", [new ModelPropertyAssignment("sortbycolumn", "x")], null)));
        Assert.Contains("partitions", ex.Message);
    }

    private static Database NewDatabase()
        => new() { Name = "M", Model = new Model { Name = "Model" } };

    private static Table AddTable(Database db, string name)
    {
        var table = new Table { Name = name };
        table.Partitions.Add(new Partition
        {
            Name = name,
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        db.Model.Tables.Add(table);
        return table;
    }

    private static SingleColumnRelationship AddRelationship(Database db)
    {
        var sales = AddTable(db, "Sales");
        var salesKey = new DataColumn { Name = "Key", DataType = DataType.Int64 };
        sales.Columns.Add(salesKey);

        var product = AddTable(db, "Product");
        var productKey = new DataColumn { Name = "Key", DataType = DataType.Int64 };
        product.Columns.Add(productKey);

        var relationship = new SingleColumnRelationship
        {
            Name = Guid.NewGuid().ToString(),
            FromColumn = salesKey,
            ToColumn = productKey,
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        };
        db.Model.Relationships.Add(relationship);
        return relationship;
    }
}
