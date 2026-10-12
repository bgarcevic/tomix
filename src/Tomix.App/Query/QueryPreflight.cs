using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Validate;
using Tomix.Core.Dax;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.App.Query;

/// <summary>
/// The <c>query</c> pre-flight: checks the tables, columns and measures a DAX query names
/// against the model's metadata before the query is sent, so a typo comes back at once with
/// the closest name instead of as a server error. It reads the session's in-memory snapshot,
/// so it costs no extra round trip.
/// </summary>
/// <remarks>
/// It blocks the query, so it reports only what the server would reject too: a reference
/// whose name nothing in the model or the query itself defines. Anything it cannot judge
/// passes — a query that does not parse (the server reports the syntax), a DMV query, a model
/// with no tables, columns of a calculated or query-defined table, and a bare <c>[X]</c> that
/// matches a column the query builds itself, wherever it is used.
/// </remarks>
internal static class QueryPreflight
{
    /// <summary>Tables or columns listed in the hint when no name is close to the miss.</summary>
    private const int MaxListed = 20;

    /// <summary>A reference the model cannot resolve.</summary>
    /// <param name="Reference">The reference as written, e.g. <c>Sales[Amout]</c>.</param>
    /// <param name="Message">What cannot be found.</param>
    /// <param name="Suggestion">The closest existing name, or null when nothing is close.</param>
    /// <param name="Hint">The did-you-mean, or a list of what exists when nothing is close.</param>
    /// <param name="Line">The 1-based line of the reference in the query.</param>
    internal sealed record Miss(string Reference, string Message, string? Suggestion, string? Hint, int Line);

    internal static IReadOnlyList<Miss> Check(string query, ModelSnapshot snapshot)
    {
        if (!QueryModelHandler.FirstSignificantToken(query).Equals("EVALUATE", StringComparison.OrdinalIgnoreCase)
            && !QueryModelHandler.FirstSignificantToken(query).Equals("DEFINE", StringComparison.OrdinalIgnoreCase))
            return [];

        var index = ModelNameIndex.Build(ModelObjectProjection.Flatten(snapshot));
        if (index.TableColumns.Count == 0 || DaxSyntaxCheck.Analyze(query).Count > 0)
            return [];

        var defined = QueryDefinitions.Read(query);
        IReadOnlyDictionary<int, DaxQueryColumnScope>? queryColumns = null;
        var misses = new List<Miss>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in DaxReferenceExtractor.Extract(query))
        {
            var text = query[reference.Start..(reference.End + 1)];
            Miss? miss = null;
            switch (reference.Shape)
            {
                case DaxReferenceShape.Qualified:
                    miss = CheckQualified(reference, text, index, defined);
                    break;

                // 'Name' is a table, a calendar (TOTALYTD([X], 'Fiscal')) or a DEFINE TABLE.
                case DaxReferenceShape.Table:
                    if (!index.TableColumns.ContainsKey(reference.Table!)
                        && !index.CalendarNames.Contains(reference.Table!)
                        && !defined.Tables.Contains(reference.Table!))
                        miss = TableMiss(reference.Table!, text, index, includeCalendars: true);
                    break;

                case DaxReferenceShape.Unqualified:
                    if (index.MeasureNames.Contains(reference.Object!)
                        || index.ColumnNames.Contains(reference.Object!)
                        || defined.Measures.Contains(reference.Object!)
                        || defined.Columns.Contains(reference.Object!))
                        break;

                    // A column the query builds itself (SUMMARIZECOLUMNS("Total", ...), ADDCOLUMNS):
                    // whether it is in scope where it is used is the server's call.
                    queryColumns ??= DaxQueryColumns.Analyze(query);
                    if (queryColumns.ContainsKey(reference.Start))
                        break;

                    var suggestion = Closest(reference.Object!, index.MeasureNames.Concat(index.ColumnNames));
                    miss = new Miss(
                        text,
                        $"Measure or column [{reference.Object}] cannot be found in the model.",
                        suggestion,
                        suggestion is null ? null : DidYouMean(suggestion),
                        0);
                    break;
            }

            if (miss is not null && seen.Add(miss.Reference))
                misses.Add(miss with { Line = SourcePosition.Of(query, reference.Start).Line });
        }

        return misses;
    }

    private static Miss? CheckQualified(
        DaxReferenceExtractor.DaxReference reference,
        string text,
        ModelNameIndex index,
        QueryDefinitions defined)
    {
        var table = reference.Table!;
        var name = reference.Object!;

        // Columns of a DEFINE TABLE, or of a calculated table nothing declares, are not knowable.
        if (defined.Tables.Contains(table) || index.UnknownColumnTables.Contains(table))
            return null;

        if (!index.TableColumns.TryGetValue(table, out var columns))
            return TableMiss(table, text, index, includeCalendars: false);

        if (columns.Contains(name)
            || index.MeasureNames.Contains(name)
            || defined.Measures.Contains(name)
            || defined.TableColumns.Contains((table, name)))
            return null;

        var suggestion = Closest(name, columns.Concat(index.MeasureNames));
        return new Miss(
            text,
            $"Column [{name}] cannot be found on table '{table}'.",
            suggestion,
            suggestion is not null
                ? DidYouMean(suggestion)
                : columns.Count is > 0 and <= MaxListed
                    ? $"Columns on '{table}': {string.Join(", ", columns.Order(StringComparer.OrdinalIgnoreCase))}."
                    : null,
            0);
    }

    private static Miss TableMiss(string table, string text, ModelNameIndex index, bool includeCalendars)
    {
        var names = includeCalendars
            ? index.TableColumns.Keys.Concat(index.CalendarNames).ToList()
            : index.TableColumns.Keys.ToList();
        var suggestion = Closest(table, names);
        return new Miss(
            text,
            $"Table '{table}' cannot be found.",
            suggestion,
            suggestion is not null
                ? DidYouMean(suggestion)
                : names.Count <= MaxListed
                    ? $"Tables: {string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase))}."
                    : null,
            0);
    }

    private static string DidYouMean(string suggestion) => $"Did you mean '{suggestion}'?";

    /// <summary>
    /// The candidate nearest to <paramref name="name"/>; a tie goes to the alphabetically first,
    /// so the hint does not depend on model order.
    /// </summary>
    private static string? Closest(string name, IEnumerable<string> candidates)
        => NameSuggestion.Closest(
            name,
            candidates.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
            NameSuggestion.ScaledLimit(name));

    /// <summary>
    /// What the query's own <c>DEFINE</c> block adds to the model: <c>MEASURE T[X]</c>,
    /// <c>COLUMN T[X]</c> and <c>TABLE T</c>. Read from the tokens, so a name inside a string or
    /// comment never counts.
    /// </summary>
    private sealed record QueryDefinitions(
        HashSet<string> Measures,
        HashSet<string> Columns,
        HashSet<(string Table, string Column)> TableColumns,
        HashSet<string> Tables)
    {
        public static QueryDefinitions Read(string query)
        {
            var defined = new QueryDefinitions(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<(string, string)>(TableColumnComparer.Instance),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var tokens = DaxLexicalTokens.Read(query);
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Kind != DaxLexemeKind.Identifier)
                    continue;

                var keyword = tokens[i].Name;
                var table = tokens[i + 1];
                if (table.Kind is not (DaxLexemeKind.Identifier or DaxLexemeKind.QuotedTable))
                    continue;

                if (keyword.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    defined.Tables.Add(table.Name);
                    continue;
                }

                var isMeasure = keyword.Equals("MEASURE", StringComparison.OrdinalIgnoreCase);
                if ((!isMeasure && !keyword.Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
                    || i + 2 >= tokens.Count
                    || tokens[i + 2].Kind != DaxLexemeKind.ColumnReference)
                    continue;

                var name = tokens[i + 2].Name;
                (isMeasure ? defined.Measures : defined.Columns).Add(name);
                defined.TableColumns.Add((table.Name, name));
            }

            return defined;
        }
    }

    private sealed class TableColumnComparer : IEqualityComparer<(string Table, string Column)>
    {
        public static readonly TableColumnComparer Instance = new();

        public bool Equals((string Table, string Column) x, (string Table, string Column) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Column, y.Column);

        public int GetHashCode((string Table, string Column) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }
}
