using Tomix.App.Vertipaq;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.App.Tests;

public sealed class VertipaqAnnotationBuilderTests
{
    [Fact]
    public void Build_EmitsModelRootAnnotations_WithInvariantValues()
    {
        var targets = VertipaqAnnotationBuilder.Build(NewStats());

        var root = Assert.Single(targets, t => t.Path == ".");
        Assert.Null(root.Type);
        Assert.Contains(root.Assignments, a => a is { Property: "Annotation:Vertipaq_TotalSize", Value: "1430" });
        Assert.Contains(root.Assignments, a => a is { Property: "Annotation:Vertipaq_TableCount", Value: "2" });
        Assert.Contains(root.Assignments, a => a is { Property: "Annotation:Vertipaq_ColumnCount", Value: "3" });
        Assert.Contains(root.Assignments, a => a is
        {
            Property: "Annotation:Vertipaq_ExtractionDate",
            Value: "2026-07-14T12:00:00Z"
        });
    }

    [Fact]
    public void Build_EmitsTableAnnotations_WithTheBpaRowCountKey()
    {
        var targets = VertipaqAnnotationBuilder.Build(NewStats());

        var sales = Assert.Single(targets, t => t.Path == "'Sales'");
        Assert.Equal(ModelObjectKind.Table, sales.Type);
        // Singular RowCount: the bundled LARGE_TABLES_SHOULD_BE_PARTITIONED rule reads it.
        Assert.Contains(sales.Assignments, a => a is { Property: "Annotation:Vertipaq_RowCount", Value: "1000" });
        Assert.Contains(sales.Assignments, a => a is { Property: "Annotation:Vertipaq_TableSize", Value: "1300" });
    }

    [Fact]
    public void Build_EmitsColumnAnnotations_AndSkipsRowNumberColumns()
    {
        var targets = VertipaqAnnotationBuilder.Build(NewStats());

        var amount = Assert.Single(targets, t => t.Path == "'Sales'/'Amount'");
        Assert.Equal(ModelObjectKind.Column, amount.Type);
        Assert.Contains(amount.Assignments, a => a is { Property: "Annotation:Vertipaq_Cardinality", Value: "900" });
        Assert.Contains(amount.Assignments, a => a is { Property: "Annotation:Vertipaq_Encoding", Value: "VALUE" });

        Assert.DoesNotContain(targets, t => t.Path.Contains("RowNumber"));
    }

    [Fact]
    public void Build_EmitsRelationshipAnnotations_ForTheRiViolationRule()
    {
        var targets = VertipaqAnnotationBuilder.Build(NewStats());

        var relationship = Assert.Single(targets, t => t.Type == ModelObjectKind.Relationship);
        Assert.Equal("'Sales'[ProductKey]->'Product'[ProductKey]", relationship.Path);
        Assert.Contains(relationship.Assignments, a => a is { Property: "Annotation:Vertipaq_RIViolationInvalidRows", Value: "1" });
        Assert.Contains(relationship.Assignments, a => a is { Property: "Annotation:Vertipaq_MissingKeys", Value: "2" });
        Assert.Contains(relationship.Assignments, a => a is { Property: "Annotation:Vertipaq_UsedSize", Value: "24" });
    }

    [Fact]
    public void Build_AlwaysQuotesSegments_SoKeywordNamesStayLiteral()
    {
        var stats = NewStats() with
        {
            Tables =
            [
                new VertipaqTableStats("Measures", 1, 1, 1, 1, 0, 0, 0, 0, 100, 1, 1, 1, true)
            ],
            Columns =
            [
                NewColumn("Measures", "Columns", 1, "HASH")
            ],
            Relationships = []
        };

        var targets = VertipaqAnnotationBuilder.Build(stats);

        // Unquoted, these names would be consumed as container keywords by the path parser.
        Assert.Contains(targets, t => t.Path == "'Measures'");
        Assert.Contains(targets, t => t.Path == "'Measures'/'Columns'");
    }

    [Fact]
    public void Build_EscapesApostrophesAndSlashes_InQuotedSegments()
    {
        var stats = NewStats() with
        {
            Tables =
            [
                new VertipaqTableStats("A/B", 1, 1, 1, 1, 0, 0, 0, 0, 100, 1, 1, 1, true)
            ],
            Columns =
            [
                NewColumn("A/B", "KPI'er", 1, "HASH")
            ],
            Relationships = []
        };

        var targets = VertipaqAnnotationBuilder.Build(stats);

        Assert.Contains(targets, t => t.Path == "'A/B'");
        Assert.Contains(targets, t => t.Path == "'A/B'/'KPI''er'");
    }

    private static VertipaqModelStats NewStats()
        => new(
            "SalesModel", "server", new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            TotalSize: 1430, TableCount: 2, ColumnCount: 3, MaxRowCount: 1000,
            Tables:
            [
                new VertipaqTableStats("Sales", 1000, 1300, 1300, 800, 500, 0, 24, 0, 89.4, 2, 1, 2, true)
            ],
            Columns:
            [
                NewColumn("Sales", "Amount", 900, "VALUE"),
                NewColumn("Sales", "RowNumber-2662979B", 0, "HASH", isRowNumber: true)
            ],
            Relationships:
            [
                new VertipaqRelationshipStats(
                    "'Sales'[ProductKey] -> 'Product'[ProductKey]",
                    "Sales", "Product", "'Sales'[ProductKey]", "'Product'[ProductKey]",
                    24, 100, 100, 2, 1, 0.1, true, "OneDirection")
            ],
            Partitions: []);

    private static VertipaqColumnStats NewColumn(
        string table, string name, long cardinality, string encoding, bool isRowNumber = false)
        => new(
            table, name, cardinality, "Int64", encoding, 1000, 600, 400, 0,
            10, 20, 0.1, 1, 1, false, true, isRowNumber, "Ready");
}
