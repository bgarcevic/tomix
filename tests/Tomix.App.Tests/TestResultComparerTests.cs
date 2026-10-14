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

    // ── Tolerance and row order ──────────────────────────────────────────────

    private static readonly IReadOnlyList<QueryColumn> MeasureColumns =
        [new QueryColumn("Sales[Region]", "string"), new QueryColumn("[Avg]", "double")];

    private static TestComparison CompareAvg(string expected, double actual, string? tolerance)
    {
        TestTolerance? parsed = null;
        if (tolerance is not null)
            Assert.True(TestTolerance.TryParse(tolerance, out parsed, out _));

        return TestResultComparer.Compare(
            new TestSnapshot(1, "hash", MeasureColumns, [["East", expected]]),
            Actual(MeasureColumns, ["East", actual]),
            new TestCompareOptions(Tolerance: parsed));
    }

    [Theory]
    [InlineData("0.3", 0.30000000000000004, null, false)]  // exact: float noise fails
    [InlineData("0.3", 0.30000000000000004, "1e-9", true)]
    [InlineData("100", 100.4, "0.5", true)]
    [InlineData("100", 100.6, "0.5", false)]
    [InlineData("1000", 1009, "1%", true)]                 // relative to the larger value
    [InlineData("1000", 1011, "1%", false)]
    public void Compare_NumericTolerance(string expected, double actual, string? tolerance, bool passes)
        => Assert.Equal(passes, CompareAvg(expected, actual, tolerance).Passed);

    [Fact]
    public void Compare_Tolerance_ReportsTheActualValueWhenOutside()
    {
        var difference = Assert.Single(CompareAvg("100", 100.6, "0.5").Differences);

        Assert.Equal("100", difference.Expected);
        Assert.Equal("100.6", difference.Actual);
    }

    [Fact]
    public void Compare_Tolerance_DoesNotApplyToTextColumns()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["1", "5"]),
            Actual(rows: [["1.001", 5m]]),
            new TestCompareOptions(Tolerance: new TestTolerance(0.1, Relative: false)));

        Assert.False(comparison.Passed);
        Assert.Equal("Sales[Region]", Assert.Single(comparison.Differences).Column);
    }

    [Fact]
    public void Compare_Tolerance_NeverMatchesBlankToANumber()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", null]),
            Actual(rows: [["East", 0m]]),
            new TestCompareOptions(Tolerance: new TestTolerance(1, Relative: false)));

        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Compare_RowOrderMatters_ByDefault()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"], ["West", "2"]),
            Actual(rows: [["West", 2m], ["East", 1m]]));

        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Compare_IgnoreRowOrder_PassesForTheSameRowsInAnotherOrder()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"], ["West", "2"], ["North", null]),
            Actual(rows: [["West", 2m], ["North", null], ["East", 1m]]),
            new TestCompareOptions(IgnoreRowOrder: true));

        Assert.True(comparison.Passed);
    }

    [Fact]
    public void Compare_IgnoreRowOrder_StillReportsAChangedRow()
    {
        var comparison = TestResultComparer.Compare(
            Snapshot(["East", "1"], ["West", "2"]),
            Actual(rows: [["West", 3m], ["East", 1m]]),
            new TestCompareOptions(IgnoreRowOrder: true));

        Assert.False(comparison.Passed);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("2", difference.Expected);
        Assert.Equal("3", difference.Actual);
    }

    [Fact]
    public void Compare_IgnoreRowOrder_SortsByDimensionsBeforeMeasures()
    {
        // The measure is the first column. Sorting whole rows as text would order the actual
        // side B, A (2 < 2.0000001) against the expected A, B; dimensions sort first, so
        // tolerance-sized noise cannot reorder rows.
        IReadOnlyList<QueryColumn> columns = [new QueryColumn("[Avg]", "double"), new QueryColumn("Sales[Region]", "string")];
        var comparison = TestResultComparer.Compare(
            new TestSnapshot(1, "hash", columns, [["2", "A"], ["2", "B"]]),
            Actual(columns, [2.0000001, "A"], [2.0, "B"]),
            new TestCompareOptions(new TestTolerance(1e-3, Relative: false), IgnoreRowOrder: true));

        Assert.True(comparison.Passed);
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
