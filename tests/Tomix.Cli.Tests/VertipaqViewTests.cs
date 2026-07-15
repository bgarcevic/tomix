using Tomix.Cli.Output;
using Tomix.Core.Vertipaq;
using static Tomix.Cli.Output.VertipaqView;

namespace Tomix.Cli.Tests;

public sealed class VertipaqViewTests
{
    // ── Section resolution ──────────────────────────────────────────────────

    [Fact]
    public void ResolveSections_DefaultsToColumns()
        => Assert.Equal([Section.Columns], ResolveSections(Options()));

    [Fact]
    public void ResolveSections_StatsAlone_ShowsNoDataSections()
        => Assert.Empty(ResolveSections(Options(stats: true)));

    [Fact]
    public void ResolveSections_All_ReturnsEverySectionInOrder()
        => Assert.Equal(
            [Section.Tables, Section.Columns, Section.Relationships, Section.Partitions],
            ResolveSections(Options(all: true)));

    [Fact]
    public void ResolveSections_FlagsCompose()
        => Assert.Equal(
            [Section.Tables, Section.Relationships],
            ResolveSections(Options(tables: true, relationships: true)));

    // ── Field resolution ────────────────────────────────────────────────────

    [Fact]
    public void ResolveFields_ColumnsDefaults()
        => Assert.Equal(
            ["name", "card", "size", "%tbl", "%db", "bar"],
            ResolveFields(Section.Columns, detail: false, fields: null).Select(f => f.Token));

    [Fact]
    public void ResolveFields_Detail_AddsBreakdown_AndKeepsBarLast()
    {
        var tokens = ResolveFields(Section.Columns, detail: true, fields: null).Select(f => f.Token).ToList();

        Assert.Contains("data", tokens);
        Assert.Contains("dict", tokens);
        Assert.Contains("encoding", tokens);
        Assert.Equal("bar", tokens[^1]);
    }

    [Fact]
    public void ResolveFields_ExplicitList_WinsAndIsCaseInsensitive()
        => Assert.Equal(
            ["name", "size", "bar"],
            ResolveFields(Section.Columns, detail: true, fields: ["NAME", "Size", "bar"]).Select(f => f.Token));

    [Fact]
    public void UnknownTokens_FlagsOnlyInvalidEntries()
        => Assert.Equal(["bogus"], UnknownTokens(Section.Columns, ["name", "bogus", "%db"]));

    [Fact]
    public void ParseFieldList_SplitsAndTrims()
        => Assert.Equal(["name", "card"], ParseFieldList(" name , card ,, "));

    // ── Bar ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, "░░░░░░░░░░")]
    [InlineData(100, 100, "██████████")]
    [InlineData(50, 100, "█████░░░░░")]
    [InlineData(1, 1000, "█░░░░░░░░░")] // non-zero always shows one tick
    [InlineData(10, 0, "░░░░░░░░░░")]   // zero max renders empty
    public void Bar_RendersProportionally(long value, long max, string expected)
        => Assert.Equal(expected, Bar(value, max));

    // ── Formatting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(12.34, "12.3 %")]
    [InlineData(0, "0.0 %")]
    [InlineData(double.NaN, "0.0 %")]
    public void Percent_IsInvariantOneDecimal(double value, string expected)
        => Assert.Equal(expected, Percent(value));

    // ── Section building ────────────────────────────────────────────────────

    [Fact]
    public void BuildSections_SortsBySizeDescending_AndAppliesTop()
    {
        var section = Assert.Single(BuildSections(Stats(), Options(top: 1)));

        Assert.Equal(Section.Columns, section.Section);
        Assert.Equal(3, section.TotalCount);
        var row = Assert.Single(section.Rows);
        Assert.Equal("Sales[Amount]", row[0]); // largest column first
    }

    [Fact]
    public void BuildSections_BarIsRelativeToSectionMax()
    {
        var section = Assert.Single(BuildSections(Stats(), Options()));
        var barIndex = section.Fields.ToList().FindIndex(f => f.Kind == FieldKind.Bar);

        Assert.Equal("██████████", section.Rows[0][barIndex]); // the max row
        Assert.Equal("███░░░░░░░", section.Rows[1][barIndex]); // 300 of 1000
    }

    [Fact]
    public void BuildSections_EmptyStats_YieldsEmptyRows()
    {
        var stats = Stats() with { Columns = [], Tables = [], Relationships = [], Partitions = [] };
        var section = Assert.Single(BuildSections(stats, Options()));

        Assert.Empty(section.Rows);
        Assert.Equal(0, section.TotalCount);
    }

    // ── Summary ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSummary_IncludesTotalsAndLargestObjects()
    {
        var summary = BuildSummary(Stats());

        Assert.Contains(summary, e => e is { Label: "Total size", Value: "1,430 B" });
        Assert.Contains(summary, e => e.Label == "Largest table" && e.Value.StartsWith("Sales"));
        Assert.Contains(summary, e => e.Label == "Largest column" && e.Value.StartsWith("Sales[Amount]"));
        Assert.Contains(summary, e => e is { Label: "Extracted", Value: "2026-07-14 12:00 UTC" });
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static ViewOptions Options(
        bool tables = false,
        bool columns = false,
        bool relationships = false,
        bool partitions = false,
        bool all = false,
        bool detail = false,
        bool stats = false,
        IReadOnlyList<string>? fields = null,
        int? top = null)
        => new(tables, columns, relationships, partitions, all, detail, stats, fields, top);

    private static VertipaqModelStats Stats()
        => new(
            "SalesModel", "server", new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            TotalSize: 1430, TableCount: 2, ColumnCount: 3, MaxRowCount: 1000,
            Tables:
            [
                new("Sales", 1000, 1300, 1300, 800, 500, 0, 24, 0, 90.9, 2, 1, 2, true),
                new("Product", 100, 130, 130, 80, 50, 0, 0, 0, 9.1, 1, 1, 1, true)
            ],
            Columns:
            [
                Column("Sales", "Amount", 1000),
                Column("Sales", "ProductKey", 300),
                Column("Product", "ProductKey", 130)
            ],
            Relationships:
            [
                new("'Sales'[ProductKey] -> 'Product'[ProductKey]",
                    "Sales", "Product", "'Sales'[ProductKey]", "'Product'[ProductKey]",
                    24, 100, 100, 0, 0, 0.1, true, "OneDirection")
            ],
            Partitions:
            [
                new("Sales", "Sales-Part0", 1000, 800, 1, "Read", "M", "Import", null),
                new("Product", "Product-Part0", 100, 80, 1, "Read", "M", "Import", null)
            ]);

    private static VertipaqColumnStats Column(string table, string name, long size)
        => new(
            table, name, 100, "Int64", "HASH", size, size / 2, size / 2, 0,
            10, 20, 0.1, 1, 1, false, true, false, "Ready");
}
