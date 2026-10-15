using Tomix.Core.Dax;

namespace Tomix.App.Dax;

/// <summary>Which tables a lone <c>[Col]</c> can read.</summary>
/// <param name="Tables">
/// Every table it may bind to — always including the one DAX picks. When nothing provides the
/// column (<paramref name="Unresolved"/>), every table that has it, so a caller that must not
/// miss a reader still sees them all.
/// </param>
/// <param name="Certain">
/// No unknown row context was crossed: a single table in <paramref name="Tables"/> is exactly
/// where DAX binds it, and <paramref name="Unresolved"/> means DAX cannot bind it at all.
/// </param>
/// <param name="Unresolved">No row context around the reference has the column, and neither does the own table.</param>
internal sealed record LoneColumnBinding(IReadOnlyList<string> Tables, bool Certain, bool Unresolved);

/// <summary>
/// Resolves lone <c>[Col]</c> references in one expression as DAX does: a column used as a value
/// binds to the innermost row context whose table has it, and one passed where a column is
/// expected (<c>SUM([Col])</c>) to the expression's own table. Which of the two a reference is
/// needs function signatures, so both count: the innermost row context's table and the own table.
/// </summary>
/// <param name="expression">The DAX text the references were extracted from.</param>
/// <param name="ownTable">The table the expression belongs to; null when it has none (a query, a calculated table).</param>
/// <param name="tablesWithColumn">The model tables that have a column of the given name.</param>
/// <param name="isTable">Whether the model has a table of the given name.</param>
internal sealed class LoneColumnResolver(
    string expression,
    string? ownTable,
    Func<string, IReadOnlyList<string>> tablesWithColumn,
    Func<string, bool> isTable)
{
    private IReadOnlyDictionary<int, IReadOnlyList<DaxRowContext>>? _contexts;

    /// <param name="start">The offset of the reference's opening bracket.</param>
    /// <param name="column">The bracketed name.</param>
    public LoneColumnBinding Resolve(int start, string column)
    {
        var all = tablesWithColumn(column);
        _contexts ??= DaxRowContexts.Analyze(expression);

        // Not a lone column to the parser (a hierarchy level: Product[Category].[Subcategory]).
        if (!_contexts.TryGetValue(start, out var contexts))
            return new LoneColumnBinding(all, Certain: false, Unresolved: false);

        var tables = new List<string>();
        var built = false;

        foreach (var context in contexts)
        {
            if (context.Unknown || context.Sources.Any(s => !isTable(s.Table)))
                return new LoneColumnBinding(all, Certain: false, Unresolved: false);

            var hit = false;
            foreach (var source in context.Sources)
            {
                if (source.Column is not null && !Eq(source.Column, column))
                    continue;
                if (all.FirstOrDefault(t => Eq(t, source.Table)) is { } table)
                {
                    Add(tables, table);
                    hit = true;
                }
            }

            built = context.Built.Contains(column);
            if (hit || built)
                break;
        }

        if (ownTable is not null && all.FirstOrDefault(t => Eq(t, ownTable)) is { } own)
            Add(tables, own);

        // A column the expression builds itself (ADDCOLUMNS) is no model column at all.
        if (tables.Count == 0 && built)
            return new LoneColumnBinding([], Certain: true, Unresolved: false);

        return tables.Count == 0
            ? new LoneColumnBinding(all, Certain: true, Unresolved: true)
            : new LoneColumnBinding(tables, Certain: true, Unresolved: false);
    }

    private static void Add(List<string> tables, string table)
    {
        if (!tables.Any(t => Eq(t, table)))
            tables.Add(table);
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
