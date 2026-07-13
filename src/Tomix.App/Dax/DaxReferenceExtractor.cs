using System.Text.RegularExpressions;

namespace Tomix.App.Dax;

/// <summary>
/// Extracts column/measure references from a DAX expression. Shared by dependency analysis
/// (<c>deps</c>) and the BPA engine's dependency graph so the patterns live in one place.
/// </summary>
public static partial class DaxReferenceExtractor
{
    /// <summary>A reference found in a DAX expression.</summary>
    /// <param name="Table">The table qualifier when present (<c>'Table'[X]</c> / <c>Table[X]</c>), else <c>null</c>.</param>
    /// <param name="Object">The bracketed object name.</param>
    /// <param name="FullyQualified">Whether the reference carried a table qualifier.</param>
    public readonly record struct DaxReference(string? Table, string Object, bool FullyQualified);

    /// <summary>
    /// Enumerates references in <paramref name="expression"/>. Qualified references
    /// (<c>'Table'[X]</c> or <c>Table[X]</c>) are reported with <c>FullyQualified = true</c>;
    /// lone references (<c>[X]</c> not part of a qualified reference) with <c>false</c>.
    /// </summary>
    public static IEnumerable<DaxReference> Extract(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            yield break;

        foreach (Match match in QualifiedReference().Matches(expression))
            yield return new DaxReference(match.Groups["table"].Value, match.Groups["object"].Value, FullyQualified: true);

        foreach (Match match in LoneReference().Matches(expression))
        {
            // A lone reference immediately following ']' is part of a measure/column chain we
            // already captured; skip it (mirrors the original deps behavior).
            if (match.Index > 0 && expression[match.Index - 1] == ']')
                continue;

            yield return new DaxReference(Table: null, match.Groups["object"].Value, FullyQualified: false);
        }
    }

    /// <summary>
    /// Enumerates table names referenced without a column/measure, e.g. <c>COUNTROWS('Udlån')</c>.
    /// Only quoted forms are reported: a single-quoted identifier in DAX is always a table,
    /// whereas a bare word could equally be a VAR or function name.
    /// </summary>
    public static IEnumerable<string> ExtractTableReferences(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            yield break;

        foreach (Match match in QuotedTableReference().Matches(expression))
            yield return match.Groups["table"].Value;
    }

    // A qualified reference: a quoted table name ('Order Lines') OR a bare identifier table
    // (Sales) — never just whitespace/punctuation — immediately followed by [Object].
    // (.NET permits the duplicate "table" group name across alternatives.)
    [GeneratedRegex(@"(?:'(?<table>[^']+)'|(?<table>\w+))\[(?<object>[^\]]+)\]")]
    private static partial Regex QualifiedReference();

    [GeneratedRegex("(?<![A-Za-z0-9_'])\\[(?<object>[^\\]]+)\\]")]
    private static partial Regex LoneReference();

    // A quoted table name NOT followed by [Object] — a bare table reference.
    [GeneratedRegex(@"'(?<table>[^']+)'(?!\[)")]
    private static partial Regex QuotedTableReference();
}
