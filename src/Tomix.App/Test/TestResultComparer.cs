using System.Globalization;
using Tomix.Core.Models;

namespace Tomix.App.Test;

/// <summary>
/// One divergence between a snapshot and an actual result. <see cref="Kind"/> is
/// <c>column</c>, <c>rowCount</c>, or <c>cell</c>; <see cref="Row"/> is 1-based
/// (null for non-cell differences). Serialized inside <see cref="TestCaseResult"/> —
/// property names and order are part of the JSON output contract.
/// </summary>
public sealed record TestDifference(
    string Kind,
    int? Row,
    string? Column,
    string? Expected,
    string? Actual);

/// <param name="Differences">The first differences found (capped by the caller's budget).</param>
/// <param name="TotalDifferences">The full count, including differences beyond the cap.</param>
public sealed record TestComparison(
    bool Passed,
    IReadOnlyList<TestDifference> Differences,
    int TotalDifferences);

/// <summary>
/// Pure comparison of an expected snapshot against an actual rowset. Columns compare first
/// (count, then name and type per index) and any column difference short-circuits row
/// comparison — cell diffs against a different shape are noise. Rows then compare in order
/// (ordering is significant unless <see cref="TestCompareOptions.IgnoreRowOrder"/> sorts both
/// sides first), cells by ordinal equality of their <see cref="TestValueFormatter"/> canonical
/// strings, or within <see cref="TestCompareOptions.Tolerance"/> for numeric columns.
/// </summary>
public static class TestResultComparer
{
    public const int DefaultMaxDifferences = 10;

    // "object" is a Variant-typed measure: its cells compare by value whenever both parse as
    // numbers, and exactly otherwise (text, booleans, dates).
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal) { "int64", "double", "decimal", "object" };

    public static TestComparison Compare(
        TestSnapshot expected,
        ModelQueryResult actual,
        TestCompareOptions? options = null,
        int maxDifferences = DefaultMaxDifferences)
    {
        options ??= TestCompareOptions.Exact;
        var differences = new List<TestDifference>();
        var total = 0;

        void Add(TestDifference difference)
        {
            total++;
            if (differences.Count < maxDifferences)
                differences.Add(difference);
        }

        if (expected.Columns.Count != actual.Columns.Count)
            Add(new TestDifference(
                "column",
                Row: null,
                Column: null,
                Expected: $"{expected.Columns.Count} column(s)",
                Actual: $"{actual.Columns.Count} column(s)"));

        var columnOverlap = Math.Min(expected.Columns.Count, actual.Columns.Count);
        for (var c = 0; c < columnOverlap; c++)
        {
            var (exp, act) = (expected.Columns[c], actual.Columns[c]);
            if (!string.Equals(exp.Name, act.Name, StringComparison.Ordinal)
                || !string.Equals(exp.Type, act.Type, StringComparison.Ordinal))
                Add(new TestDifference(
                    "column",
                    Row: null,
                    Column: exp.Name,
                    Expected: $"{exp.Name} ({exp.Type})",
                    Actual: $"{act.Name} ({act.Type})"));
        }

        if (total > 0)
            return new TestComparison(false, differences, total);

        if (expected.Rows.Count != actual.Rows.Count)
            Add(new TestDifference(
                "rowCount",
                Row: null,
                Column: null,
                Expected: $"{expected.Rows.Count} row(s)",
                Actual: $"{actual.Rows.Count} row(s)"));

        var numeric = expected.Columns.Select(column => NumericTypes.Contains(column.Type)).ToArray();
        IReadOnlyList<IReadOnlyList<string?>> expectedRows = expected.Rows;
        IReadOnlyList<IReadOnlyList<string?>> actualRows = actual.Rows
            .Select(row => (IReadOnlyList<string?>)row.Select(TestValueFormatter.Format).ToList())
            .ToList();

        if (options.IgnoreRowOrder)
        {
            var rowComparer = new RowComparer(numeric);
            expectedRows = expectedRows.Order(rowComparer).ToList();
            actualRows = actualRows.Order(rowComparer).ToList();
        }

        // Still cell-compare the overlapping prefix so the report shows what diverged.
        var rowOverlap = Math.Min(expectedRows.Count, actualRows.Count);
        for (var r = 0; r < rowOverlap; r++)
        {
            for (var c = 0; c < expected.Columns.Count; c++)
            {
                var expectedCell = expectedRows[r][c];
                var actualCell = actualRows[r][c];
                if (!CellsMatch(expectedCell, actualCell, numeric[c] ? options.Tolerance : null))
                    Add(new TestDifference(
                        "cell",
                        Row: r + 1,
                        Column: expected.Columns[c].Name,
                        Expected: expectedCell,
                        Actual: actualCell));
            }
        }

        return new TestComparison(total == 0, differences, total);
    }

    private static bool CellsMatch(string? expected, string? actual, TestTolerance? tolerance)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return true;

        // BLANK never matches a number, however close; only two numbers compare within tolerance.
        return tolerance is not null
            && TryParseNumber(expected, out var expectedNumber)
            && TryParseNumber(actual, out var actualNumber)
            && tolerance.Accepts(expectedNumber, actualNumber);
    }

    private static bool TryParseNumber(string? cell, out double value)
    {
        value = 0;
        return cell is not null
            && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Orders rows for <see cref="TestCompareOptions.IgnoreRowOrder"/>: non-numeric columns first
    /// (ordinal), then numeric columns by value, so tolerance-sized noise in a measure cannot
    /// reorder rows that a dimension column already separates. Within a numeric column BLANK
    /// sorts first, then numbers by value, then any text (a Variant column can mix them).
    /// </summary>
    private sealed class RowComparer(bool[] numeric) : IComparer<IReadOnlyList<string?>>
    {
        public int Compare(IReadOnlyList<string?>? x, IReadOnlyList<string?>? y)
        {
            foreach (var pass in (bool[])[false, true])
            {
                for (var c = 0; c < numeric.Length; c++)
                {
                    if (numeric[c] != pass)
                        continue;

                    var order = pass ? CompareNumbers(x![c], y![c]) : string.CompareOrdinal(x![c], y![c]);
                    if (order != 0)
                        return order;
                }
            }

            return 0;
        }

        // Ranks BLANK, numbers, text rather than falling back to ordinal per pair, which would
        // be cyclic for mixed cells (9 < 10 < "1A" < 9) and leave the order to the input.
        private static int CompareNumbers(string? x, string? y)
            => (TryParseNumber(x, out var a), TryParseNumber(y, out var b)) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => y is null ? 1 : -1,
                (false, true) => x is null ? -1 : 1,
                _ => string.CompareOrdinal(x, y)
            };
    }
}
