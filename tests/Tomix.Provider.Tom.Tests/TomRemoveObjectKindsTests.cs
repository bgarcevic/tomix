using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Removal coverage for the object kinds beyond table children and roles: relationships,
/// levels, calculation items, role members, perspectives, cultures, shared expressions,
/// functions, and data sources. Every kind the mutation resolver can address must also be
/// removable — anything else is a create/delete asymmetry with <c>add</c>.
/// </summary>
public sealed class TomRemoveObjectKindsTests
{
    [Fact]
    public void RemoveRelationship_ByEndpointsPath()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales[CustomerId] -> Customer[Id]"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Relationships);
    }

    [Fact]
    public void RemoveRelationship_RemovesVariationsBoundToIt()
    {
        var db = BaseModel();
        var relationship = (SingleColumnRelationship)db.Model.Relationships[0];
        var customer = db.Model.Tables["Customer"];
        db.Model.Tables["Sales"].Columns["CustomerId"].Variations.Add(new Variation
        {
            Name = "Variation",
            Relationship = relationship,
            DefaultColumn = customer.Columns["Id"]
        });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales[CustomerId] -> Customer[Id]"));

        Assert.Empty(db.Model.Tables["Sales"].Columns["CustomerId"].Variations);
        Assert.Contains("variation on 'Sales'[CustomerId]", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveLevel_KeepsHierarchyWithRemainingLevels()
    {
        var db = BaseModel();
        AddHierarchy(db, "Calendar", "MonthNo", "MonthName");
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/Calendar/MonthNo"));

        Assert.True(result.Changed);
        var hierarchy = Assert.Single(db.Model.Tables["Sales"].Hierarchies);
        Assert.Equal("MonthName", Assert.Single(hierarchy.Levels).Name);
    }

    [Fact]
    public void RemoveLevel_LastLevel_RemovesHierarchy()
    {
        var db = BaseModel();
        AddHierarchy(db, "Calendar", "MonthNo");
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Sales/Calendar/MonthNo"));

        Assert.Empty(db.Model.Tables["Sales"].Hierarchies);
        Assert.Contains("hierarchy 'Sales'[Calendar] (no levels left)", result.CascadeRemoved!);
    }

    [Fact]
    public void RemoveCalculationItem()
    {
        var db = BaseModel();
        var calcGroup = new Table { Name = "Time Intelligence", CalculationGroup = new CalculationGroup() };
        calcGroup.Partitions.Add(new Partition
        {
            Name = "Time Intelligence",
            Mode = ModeType.Import,
            Source = new CalculationGroupSource()
        });
        calcGroup.CalculationGroup.CalculationItems.Add(new CalculationItem
        {
            Name = "YTD",
            Expression = "CALCULATE(SELECTEDMEASURE(), DATESYTD('Sales'[MonthNo]))"
        });
        db.Model.Tables.Add(calcGroup);
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Time Intelligence/YTD"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Tables["Time Intelligence"].CalculationGroup.CalculationItems);
    }

    [Fact]
    public void RemoveRoleMember()
    {
        var db = BaseModel();
        var role = new ModelRole { Name = "Readers" };
        role.Members.Add(new ExternalModelRoleMember { MemberName = "user@contoso.com", IdentityProvider = "AzureAD" });
        db.Model.Roles.Add(role);
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Readers/user@contoso.com"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Roles["Readers"].Members);
        Assert.Single(db.Model.Roles);
    }

    [Fact]
    public void RemovePerspective()
    {
        var db = BaseModel();
        db.Model.Perspectives.Add(new Perspective { Name = "Reporting" });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Reporting"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Perspectives);
    }

    [Fact]
    public void RemoveCulture()
    {
        var db = BaseModel();
        db.Model.Cultures.Add(new Culture { Name = "da-DK" });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("da-DK"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Cultures);
    }

    [Fact]
    public void RemoveExpression()
    {
        var db = BaseModel();
        db.Model.Expressions.Add(new NamedExpression
        {
            Name = "Environment",
            Kind = ExpressionKind.M,
            Expression = "\"DEV\" meta [IsParameterQuery=true]"
        });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Environment"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Expressions);
    }

    [Fact]
    public void RemoveFunction()
    {
        var db = BaseModel();
        db.CompatibilityLevel = 1702; // DAX user-defined functions require CL 1702+
        db.Model.Functions.Add(new Function { Name = "AddOne", Expression = "(x) => x + 1" });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("AddOne"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.Functions);
    }

    [Fact]
    public void RemoveDataSource_Unreferenced()
    {
        var db = BaseModel();
        db.Model.DataSources.Add(new ProviderDataSource { Name = "Warehouse", ConnectionString = "Data Source=sql;" });
        var mutator = new TomModelMutator(db);

        var result = mutator.RemoveObject(Remove("Warehouse"));

        Assert.True(result.Changed);
        Assert.Empty(db.Model.DataSources);
    }

    [Fact]
    public void RemoveDataSource_ReferencedByQueryPartition_Throws()
    {
        var db = BaseModel();
        var source = new ProviderDataSource { Name = "Warehouse", ConnectionString = "Data Source=sql;" };
        db.Model.DataSources.Add(source);
        var sales = db.Model.Tables["Sales"];
        sales.Partitions.Add(new Partition
        {
            Name = "FromWarehouse",
            Mode = ModeType.Import,
            Source = new QueryPartitionSource { DataSource = source, Query = "SELECT 1" }
        });
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.RemoveObject(Remove("Warehouse")));

        Assert.Contains("Sales/FromWarehouse", ex.Message);
        Assert.Single(db.Model.DataSources);
    }

    private static void AddHierarchy(Database db, string name, params string[] levelColumns)
    {
        var sales = db.Model.Tables["Sales"];
        var hierarchy = new Hierarchy { Name = name };
        for (var i = 0; i < levelColumns.Length; i++)
            hierarchy.Levels.Add(new Level { Name = levelColumns[i], Column = sales.Columns[levelColumns[i]], Ordinal = i });
        sales.Hierarchies.Add(hierarchy);
    }

    private static ModelObjectRemoveRequest Remove(string path)
        => new(path, Type: null, IfExists: false);

    private static Database BaseModel()
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
