using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Relationship creation via the arrow path form ('Sales'[Key]->'Product'[Key]), with or without
/// an explicit <c>-t relationship</c> or a <c>relationships/</c> keyword prefix.
/// </summary>
public sealed class TomAddRelationshipTests
{
    [Theory]
    [InlineData("Sales[Key]->Product[Key]")]
    [InlineData("'Sales'[Key]->'Product'[Key]")]
    [InlineData("Sales[Key] -> Product[Key]")]
    public void ArrowPath_WithoutType_CreatesManyToOneRelationship(string path)
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add(path, type: null));

        var relationship = Assert.IsType<SingleColumnRelationship>(Assert.Single(db.Model.Relationships));
        Assert.Equal("Sales", relationship.FromColumn.Table.Name);
        Assert.Equal("Key", relationship.FromColumn.Name);
        Assert.Equal("Product", relationship.ToColumn.Table.Name);
        Assert.Equal("Key", relationship.ToColumn.Name);
        Assert.Equal(RelationshipEndCardinality.Many, relationship.FromCardinality);
        Assert.Equal(RelationshipEndCardinality.One, relationship.ToCardinality);
        Assert.False(string.IsNullOrWhiteSpace(relationship.Name));
        Assert.True(result.Changed);
        Assert.Equal("Sales[Key] -> Product[Key]", result.Path);
    }

    [Fact]
    public void ExplicitType_WithArrowPath_Creates()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("Sales[Key]->Product[Key]", "Relationship"));

        Assert.True(result.Changed);
        Assert.Single(db.Model.Relationships);
    }

    [Fact]
    public void KeywordPath_Creates()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(Add("relationships/Sales[Key]->Product[Key]", type: null));

        Assert.True(result.Changed);
        Assert.Single(db.Model.Relationships);
    }

    [Fact]
    public void ExplicitType_WithoutArrow_ThrowsUsageHint()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<ArgumentException>(() => mutator.AddObject(Add("Sales/Key", "Relationship")));
        Assert.Contains("Sales[Key]->Product[Key]", ex.Message);
    }

    [Fact]
    public void MissingTable_Throws()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.AddObject(
            Add("Nope[Key]->Product[Key]", null)));
        Assert.Contains("Table not found: Nope", ex.Message);
    }

    [Fact]
    public void MissingColumn_Throws()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.AddObject(
            Add("Sales[Nope]->Product[Key]", null)));
        Assert.Contains("Column not found: Sales[Nope]", ex.Message);
    }

    [Fact]
    public void Duplicate_Throws()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);
        mutator.AddObject(Add("Sales[Key]->Product[Key]", null));

        var ex = Assert.Throws<InvalidOperationException>(() => mutator.AddObject(
            Add("Sales[Key]->Product[Key]", null)));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Duplicate_WithIfNotExists_ReturnsUnchanged()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);
        mutator.AddObject(Add("Sales[Key]->Product[Key]", null));

        var result = mutator.AddObject(Add("Sales[Key]->Product[Key]", null) with { IfNotExists = true });

        Assert.False(result.Changed);
        Assert.Single(db.Model.Relationships);
    }

    [Fact]
    public void Properties_ApplyToNewRelationship()
    {
        var db = TwoTables();
        var mutator = new TomModelMutator(db);

        mutator.AddObject(Add("Sales[Key]->Product[Key]", null) with
        {
            Properties =
            [
                new ModelPropertyAssignment("isActive", "false"),
                new ModelPropertyAssignment("crossFilteringBehavior", "BothDirections")
            ]
        });

        var relationship = Assert.IsType<SingleColumnRelationship>(Assert.Single(db.Model.Relationships));
        Assert.False(relationship.IsActive);
        Assert.Equal(CrossFilteringBehavior.BothDirections, relationship.CrossFilteringBehavior);
    }

    private static ModelObjectAddRequest Add(string path, string? type)
        => new(path, type, Value: null, [], IfNotExists: false);

    private static Database TwoTables()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        foreach (var name in new[] { "Sales", "Product" })
        {
            var table = new Table { Name = name };
            table.Columns.Add(new DataColumn { Name = "Key", DataType = DataType.Int64, SourceColumn = "Key" });
            table.Partitions.Add(new Partition
            {
                Name = name,
                Mode = ModeType.Import,
                Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
            });
            db.Model.Tables.Add(table);
        }

        return db;
    }
}
