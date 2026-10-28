using System.Text.RegularExpressions;
using Tomix.Core.Models;

namespace Tomix.App.Test;

/// <summary>
/// Checks a test states with <c>tx:</c> directives instead of (or on top of) a snapshot: the
/// result's row count (<c>rows=</c>, <c>rows&gt;=</c>, <c>rows&lt;=</c>), the columns it must have
/// (<c>columns=</c>), or that the query must fail (<c>expect-error</c>). A test with a result
/// assertion needs no snapshot unless it also states <c>snapshot</c>; one that expects an error
/// never has one.
/// </summary>
/// <param name="Rows">Exact row count.</param>
/// <param name="MinRows">Minimum row count.</param>
/// <param name="MaxRows">Maximum row count.</param>
/// <param name="Columns">Columns the result must contain, in any order; others may follow.</param>
/// <param name="ExpectError">The query must fail.</param>
/// <param name="ErrorPattern">With <paramref name="ExpectError"/>, a case-insensitive regex the error message must match; null accepts any error.</param>
/// <param name="Snapshot">The test keeps a snapshot alongside its result assertions: it is recorded and compared like any other.</param>
public sealed record TestAssertions(
    int? Rows = null,
    int? MinRows = null,
    int? MaxRows = null,
    IReadOnlyList<string>? Columns = null,
    bool ExpectError = false,
    Regex? ErrorPattern = null,
    bool Snapshot = false)
{
    public static readonly TestAssertions None = new();

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>True when the test asserts on the rowset.</summary>
    public bool ChecksResult => Rows is not null || MinRows is not null || MaxRows is not null || Columns is not null;

    /// <summary>
    /// True when the test is compared with a snapshot it must have (Missing without one) and
    /// which recording creates: every test except one whose result assertions stand in for it.
    /// </summary>
    public bool UsesSnapshot => !ChecksResult || Snapshot;

    /// <summary>Compiles an <c>expect-error=</c> pattern; null with an <paramref name="error"/> when it is not a valid regex.</summary>
    public static Regex? ParsePattern(string pattern, out string? error)
    {
        try
        {
            error = null;
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid directive 'expect-error={pattern}': not a valid regular expression ({ex.Message}).";
            return null;
        }
    }

    /// <summary>The result assertions <paramref name="result"/> breaks, one message each; empty when all hold.</summary>
    public IReadOnlyList<string> Check(ModelQueryResult result)
    {
        var failures = new List<string>();
        var rowCount = result.Rows.Count;
        if (Rows is { } rows && rowCount != rows)
            failures.Add($"Expected {rows} row(s), got {rowCount}.");
        if (MinRows is { } minRows && rowCount < minRows)
            failures.Add($"Expected at least {minRows} row(s), got {rowCount}.");
        if (MaxRows is { } maxRows && rowCount > maxRows)
            failures.Add($"Expected at most {maxRows} row(s), got {rowCount}.");

        if (Columns is { } columns)
        {
            var actual = result.Columns.Select(c => NormalizeColumn(c.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = columns.Where(c => !actual.Contains(NormalizeColumn(c))).ToList();
            if (missing.Count > 0)
                failures.Add($"Missing column(s): {string.Join(", ", missing)}. The result has: {string.Join(", ", result.Columns.Select(c => c.Name))}.");
        }

        return failures;
    }

    /// <summary>
    /// For an <see cref="ExpectError"/> test: null when the query failed with a matching error,
    /// otherwise why the test fails. <paramref name="queryError"/> is the engine's message, or
    /// null when the query returned <paramref name="result"/>.
    /// </summary>
    public string? CheckError(ModelQueryResult? result, string? queryError)
    {
        if (queryError is null)
            return $"Expected the query to fail, but it returned {result?.Rows.Count ?? 0} row(s).";

        if (ErrorPattern is null)
            return null;

        try
        {
            return ErrorPattern.IsMatch(queryError)
                ? null
                : $"The query failed, but its error does not match '{ErrorPattern}': {queryError}";
        }
        catch (RegexMatchTimeoutException)
        {
            return $"The expect-error pattern '{ErrorPattern}' took too long to match the query's error.";
        }
    }

    // DAX names are case-insensitive and quote a table name only when it needs it, so
    // 'Sales'[Region] and sales[Region] name the same column.
    private static string NormalizeColumn(string name)
    {
        var trimmed = name.Trim();
        var close = trimmed.IndexOf("'[", StringComparison.Ordinal);
        return trimmed.StartsWith('\'') && close > 0
            ? trimmed[1..close].Replace("''", "'") + trimmed[(close + 1)..]
            : trimmed;
    }
}
