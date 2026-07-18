using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// TOM does not cascade removals: deleting a table or column used to leave relationships,
/// sort-by pointers, hierarchy levels, perspective memberships, role permissions, and
/// translations dangling — the model then failed on Update()/serialization. The mutator now
/// cascade-removes those artifacts and reports each one.
/// </summary>
public sealed class TomRemoveCascadeTests
{
    [Fact]
    public void RemoveColumn_RemovesRelationshipsTouchingIt()
    {
        var db = ModelWithRelationship();
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/CustomerId"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Relationships);
        Assert.Contains("relationship 'Sales'[CustomerId] -> 'Customer'[Id]", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveTable_RemovesRelationshipsTouchingIt()
    {
        var db = ModelWithRelationship();
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Customer"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Relationships);
        Assert.DoesNotContain(db.Model.Tables, t => t.Name == "Customer");
    }

    [Fact]
    public void RemoveColumn_ClearsSortByPointingAtIt()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        sales.Columns["MonthName"].SortByColumn = sales.Columns["MonthNo"];
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/MonthNo"));

        Assert.Null(sales.Columns["MonthName"].SortByColumn);
        Assert.Contains("sort-by on 'Sales'[MonthName] (cleared)", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveColumn_RemovesItsHierarchyLevel_KeepsHierarchyWithRemainingLevels()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        var hierarchy = new Hierarchy { Name = "Calendar" };
        hierarchy.Levels.Add(new Level { Name = "No", Column = sales.Columns["MonthNo"], Ordinal = 0 });
        hierarchy.Levels.Add(new Level { Name = "Name", Column = sales.Columns["MonthName"], Ordinal = 1 });
        sales.Hierarchies.Add(hierarchy);
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/MonthNo"));

        var remaining = Assert.Single(Assert.Single(sales.Hierarchies).Levels);
        Assert.Equal("Name", remaining.Name);
        Assert.Contains("level 'No' in hierarchy 'Sales'[Calendar]", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveColumn_RemovesHierarchyLeftWithoutLevels()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        var hierarchy = new Hierarchy { Name = "Calendar" };
        hierarchy.Levels.Add(new Level { Name = "No", Column = sales.Columns["MonthNo"], Ordinal = 0 });
        sales.Hierarchies.Add(hierarchy);
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/MonthNo"));

        Assert.Empty(sales.Hierarchies);
        Assert.Contains("hierarchy 'Sales'[Calendar] (no levels left)", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveTable_RemovesPerspectiveEntryRolePermissionAndTranslations()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];

        var perspective = new Perspective { Name = "Reporting" };
        var perspectiveTable = new PerspectiveTable { Table = sales };
        perspectiveTable.PerspectiveColumns.Add(new PerspectiveColumn { Column = sales.Columns["Amount"] });
        perspective.PerspectiveTables.Add(perspectiveTable);
        db.Model.Perspectives.Add(perspective);

        var role = new ModelRole { Name = "Analyst" };
        role.TablePermissions.Add(new TablePermission { Table = sales, FilterExpression = "TRUE()" });
        db.Model.Roles.Add(role);

        var culture = new Culture { Name = "da-DK" };
        culture.ObjectTranslations.Add(new ObjectTranslation
        {
            Object = sales,
            Property = TranslatedProperty.Caption,
            Value = "Salg"
        });
        culture.ObjectTranslations.Add(new ObjectTranslation
        {
            Object = sales.Columns["Amount"],
            Property = TranslatedProperty.Caption,
            Value = "Beløb"
        });
        db.Model.Cultures.Add(culture);

        var result = new TomModelMutator(db).RemoveObject(Remove("Sales"));

        Assert.Empty(perspective.PerspectiveTables);
        Assert.Empty(role.TablePermissions);
        Assert.Empty(culture.ObjectTranslations);
        Assert.Contains("'Reporting' perspective entry", result.CascadeRemoved!);
        Assert.Contains("table permission in role 'Analyst'", result.CascadeRemoved!);
        Assert.Contains("2 translation(s) in culture 'da-DK'", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveMeasure_RemovesPerspectiveEntryAndTranslation()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        var measure = new Measure { Name = "Total", Expression = "1" };
        sales.Measures.Add(measure);

        var perspective = new Perspective { Name = "Reporting" };
        var perspectiveTable = new PerspectiveTable { Table = sales };
        perspectiveTable.PerspectiveMeasures.Add(new PerspectiveMeasure { Measure = measure });
        perspective.PerspectiveTables.Add(perspectiveTable);
        db.Model.Perspectives.Add(perspective);

        var culture = new Culture { Name = "da-DK" };
        culture.ObjectTranslations.Add(new ObjectTranslation
        {
            Object = measure,
            Property = TranslatedProperty.Caption,
            Value = "I alt"
        });
        db.Model.Cultures.Add(culture);

        var result = new TomModelMutator(db).RemoveObject(Remove("Sales/Total"));

        Assert.Empty(perspectiveTable.PerspectiveMeasures);
        Assert.Empty(culture.ObjectTranslations);
        Assert.Contains("'Reporting' perspective entry", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveColumn_RemovesColumnPermission()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        var role = new ModelRole { Name = "Analyst" };
        var permission = new TablePermission { Table = sales };
        permission.ColumnPermissions.Add(new ColumnPermission
        {
            Column = sales.Columns["Amount"],
            MetadataPermission = MetadataPermission.None
        });
        role.TablePermissions.Add(permission);
        db.Model.Roles.Add(role);

        var result = new TomModelMutator(db).RemoveObject(Remove("Sales/Amount"));

        Assert.Empty(permission.ColumnPermissions);
        Assert.Contains("column permission in role 'Analyst'", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveLastPartition_Fails_InsteadOfCorruptingTheTable()
    {
        var db = ModelWithRelationship();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.RemoveObject(
            new ModelObjectRemoveRequest("Sales/Sales", ModelObjectKind.Partition, IfExists: false)));

        Assert.Contains("last partition", ex.Message);
        Assert.Single(db.Model.Tables["Sales"].Partitions);
    }

    [Fact]
    public void RemovePartition_WithSiblingsRemaining_Removes()
    {
        var db = ModelWithRelationship();
        var sales = db.Model.Tables["Sales"];
        sales.Partitions.Add(new Partition
        {
            Name = "Sales2",
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(
            new ModelObjectRemoveRequest("Sales/Sales2", ModelObjectKind.Partition, IfExists: false));

        Assert.True(result.Changed);
        Assert.Single(sales.Partitions);
        Assert.Null(result.CascadeRemoved);
    }

    [Fact]
    public void RemoveUnentangledMeasure_ReportsNoCascade()
    {
        var db = ModelWithRelationship();
        db.Model.Tables["Sales"].Measures.Add(new Measure { Name = "Total", Expression = "1" });

        var result = new TomModelMutator(db).RemoveObject(Remove("Sales/Total"));

        Assert.True(result.Changed);
        Assert.Null(result.CascadeRemoved);
    }

    private static ModelObjectRemoveRequest Remove(string path)
        => new(path, Type: null, IfExists: false);

    /// <summary>
    /// Sales (CustomerId, Amount, MonthName, MonthNo) many-to-one Customer (Id), one import
    /// partition per table named after the table (the Desktop default).
    /// </summary>
    private static Database ModelWithRelationship()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };

        var sales = NewTable("Sales", "CustomerId", "Amount", "MonthName", "MonthNo");
        var customer = NewTable("Customer", "Id");
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(customer);

        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "SalesToCustomer",
            FromColumn = sales.Columns["CustomerId"],
            ToColumn = customer.Columns["Id"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        });

        return db;
    }

    private static Table NewTable(string name, params string[] columns)
    {
        var table = new Table { Name = name };
        foreach (var column in columns)
            table.Columns.Add(new DataColumn { Name = column, DataType = DataType.Int64, SourceColumn = column });
        table.Partitions.Add(new Partition
        {
            Name = name,
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        return table;
    }
}
