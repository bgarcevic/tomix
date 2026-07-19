using Tomix.App.Test;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class TestResultComparerTests
{
    private static readonly IReadOnlyList<QueryColumn> Columns =
        [new QueryColumn("Sales[Region]", "string"), new QueryColumn("[Total]", "decimal")];

    private static TestSnapshot Snapshot(params IReadOnlyList<string?>[] rows)
        => new(1, "hash", Columns, rows);

    private static ModelQueryResult Actual(IReadOnlyList<QueryColumn>? columns = null, params IReadOnlyList<object?>[] rows)
        => new("s", "d", columns ?? Columns, rows, Truncated: false, DurationMs: 0);

    [Fact]
    public void Compare_Passes_WhenIdentical()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1234.50"], ["West", null]),
            Actual(rows: [["East", 1234.50m], ["West", null]]));

        Assert.True(comparison.Passed);
        Assert.Empty(comparison.Differences);
        Assert.Equal(0, comparison.TotalDifferences);
    }

    [Fact]
    public void Compare_ColumnCountMismatch_ShortCircuitsRowComparison()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"]),
            Actual(columns: [new QueryColumn("Sales[Region]", "string")], rows: [["Wrong"]]));

        Assert.False(comparison.Passed);
        Assert.All(comparison.Differences, d => Assert.Equal("column", d.Kind));
        Assert.Equal("2 column(s)", comparison.Differences[0].Expected);
        Assert.Equal("1 column(s)", comparison.Differences[0].Actual);
    }

    [Theory]
    [InlineData("Sales[Area]", "string")]  // name changed
    [InlineData("Sales[Region]", "int64")] // type changed
    public void Compare_ColumnNameOrTypeMismatch_IsColumnDifference(string name, string type)
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"]),
            Actual(columns: [new QueryColumn(name, type), Columns[1]], rows: [["East", 1m]]));

        Assert.False(comparison.Passed);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("column", difference.Kind);
        Assert.Equal("Sales[Region] (string)", difference.Expected);
        Assert.Equal($"{name} ({type})", difference.Actual);
    }

    [Fact]
    public void Compare_RowCountMismatch_StillComparesOverlappingPrefix()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"], ["West", "2"]),
            Actual(rows: [["East", 9m]]));

        Assert.False(comparison.Passed);
        Assert.Equal(2, comparison.TotalDifferences);
        Assert.Equal("rowCount", comparison.Differences[0].Kind);
        Assert.Equal("cell", comparison.Differences[1].Kind);
        Assert.Equal(1, comparison.Differences[1].Row);
        Assert.Equal("[Total]", comparison.Differences[1].Column);
        Assert.Equal("1", comparison.Differences[1].Expected);
        Assert.Equal("9", comparison.Differences[1].Actual);
    }

    [Fact]
    public void Compare_CapsReportedDifferences_ButCountsAll()
    {
        var expectedRows = Enumerable.Range(0, 20)
            .Select(i => (IReadOnlyList<string?>)[$"r{i}", "0"])
            .ToArray();
        var actualRows = Enumerable.Range(0, 20)
            .Select(i => (IReadOnlyList<object?>)[$"r{i}", 1m])
            .ToArray();

        var comparison = TestResultComparer.Compare(Snapshot(expectedRows), Actual(rows: actualRows));

        Assert.False(comparison.Passed);
        Assert.Equal(10, comparison.Differences.Count);
        Assert.Equal(20, comparison.TotalDifferences);
    }

    [Fact]
    public void Compare_NullVersusValue_IsCellDifference()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", null]),
            Actual(rows: [["East", 0m]]));

        Assert.False(comparison.Passed);
        var difference = Assert.Single(comparison.Differences);
        Assert.Null(difference.Expected);
        Assert.Equal("0", difference.Actual);
    }

    // ── Canonical value formatting ──────────────────────────────────────────

    [Fact]
    public void Format_Null_StaysNull()
        => Assert.Null(TestValueFormatter.Format(null));

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    public void Format_String_IsUnchanged(string value, string expected)
        => Assert.Equal(expected, TestValueFormatter.Format(value));

    [Fact]
    public void Format_Long_UsesInvariantCulture()
        => Assert.Equal("-1234567", TestValueFormatter.Format(-1234567L));

    [Fact]
    public void Format_Double_RoundTripsWithoutPrecisionLoss()
    {
        var value = 0.1 + 0.2;
        var formatted = TestValueFormatter.Format(value)!;

        Assert.Equal(value, double.Parse(formatted, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_Decimal_PreservesScale()
        => Assert.Equal("1234.50", TestValueFormatter.Format(1234.50m));

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Format_Bool_IsLowercase(bool value, string expected)
        => Assert.Equal(expected, TestValueFormatter.Format(value));

    [Fact]
    public void Format_DateTime_OmitsZeroFraction()
        => Assert.Equal("2024-01-15T10:30:00", TestValueFormatter.Format(new DateTime(2024, 1, 15, 10, 30, 0)));

    [Fact]
    public void Format_DateTime_KeepsFractionalSeconds()
        => Assert.Equal(
            "2024-01-15T10:30:00.1234567",
            TestValueFormatter.Format(new DateTime(2024, 1, 15, 10, 30, 0).AddTicks(1234567)));
}
