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
/// (ordering is significant; test queries should use ORDER BY), cells by ordinal equality of
/// their <see cref="TestValueFormatter"/> canonical strings.
/// </summary>
public static class TestResultComparer
{
    public const int DefaultMaxDifferences = 10;

    public static TestComparison Compare(
        TestSnapshot expected,
        ModelQueryResult actual,
        int maxDifferences = DefaultMaxDifferences)
    {
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

        // Still cell-compare the overlapping prefix so the report shows what diverged.
        var rowOverlap = Math.Min(expected.Rows.Count, actual.Rows.Count);
        for (var r = 0; r < rowOverlap; r++)
        {
            for (var c = 0; c < expected.Columns.Count; c++)
            {
                var expectedCell = expected.Rows[r][c];
                var actualCell = TestValueFormatter.Format(actual.Rows[r][c]);
                if (!string.Equals(expectedCell, actualCell, StringComparison.Ordinal))
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
}
