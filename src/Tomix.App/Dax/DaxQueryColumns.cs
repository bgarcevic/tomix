using static Tomix.App.Dax.DaxCalculatedTableColumns;

namespace Tomix.App.Dax;

/// <summary>
/// Names an expression defines for itself as query-scoped columns, which an unqualified
/// <c>[Name]</c> can resolve to without the model having such a column:
/// <list type="bullet">
/// <item>string literals in a column-name position of a table function — <c>ADDCOLUMNS</c>,
/// <c>SELECTCOLUMNS</c>, <c>SUMMARIZE</c>, <c>SUMMARIZECOLUMNS</c>, <c>GROUPBY</c>, <c>ROW</c>,
/// <c>DATATABLE</c>;</item>
/// <item><c>Value</c>/<c>ValueN</c> when the expression builds a table with
/// <c>GENERATESERIES</c> or a <c>{ ... }</c> table constructor (not the list of <c>IN { ... }</c>,
/// nor <c>DATATABLE</c>'s row literals).</item>
/// </list>
/// Lexical, not scope-aware: a name used outside the call that defines it is still accepted.
/// </summary>
internal static class DaxQueryColumns
{
    public static bool Defines(string expression, string name)
    {
        var tokens = Tokenize(expression);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataTableSpans = new List<(int Start, int End)>();
        var generatesSeries = false;

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Word || tokens[i + 1].Text != "(")
                continue;

            var function = tokens[i].Text.ToUpperInvariant();
            if (function == "GENERATESERIES")
                generatesSeries = true;

            if (!NamePositions.TryGetValue(function, out var positions))
                continue;

            var (args, end) = Arguments(tokens, i + 1);
            if (function == "DATATABLE")
                dataTableSpans.Add((i + 1, end));

            for (var a = 0; a < args.Count; a++)
            {
                if (positions(a, args.Count) && args[a] is [{ Kind: Kind.String } literal])
                    names.Add(literal.Text);
            }
        }

        if (names.Contains(name))
            return true;

        return IsValueColumn(name)
            && (generatesSeries || HasTableConstructor(tokens, dataTableSpans));
    }

    /// <summary>
    /// Which argument indexes (given the argument count) hold a new column's name. Group-by
    /// arguments are column references, not strings, so "a string literal followed by another
    /// argument" reads SUMMARIZE / SUMMARIZECOLUMNS / GROUPBY name-expression pairs.
    /// </summary>
    private static readonly Dictionary<string, Func<int, int, bool>> NamePositions = new()
    {
        ["ADDCOLUMNS"] = (index, _) => index % 2 == 1,
        ["SELECTCOLUMNS"] = (index, _) => index % 2 == 1,
        ["ROW"] = (index, _) => index % 2 == 0,
        ["DATATABLE"] = (index, count) => index % 2 == 0 && index < count - 1,
        ["SUMMARIZE"] = (index, count) => index >= 1 && index < count - 1,
        ["SUMMARIZECOLUMNS"] = (index, count) => index < count - 1,
        ["GROUPBY"] = (index, count) => index >= 1 && index < count - 1,
    };

    /// <summary>The arguments of the call opening at <paramref name="open"/>, and the index of its closing token.</summary>
    private static (List<List<Token>> Args, int End) Arguments(List<Token> tokens, int open)
    {
        var args = new List<List<Token>> { new() };
        var depth = 0;

        for (var i = open; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Symbol && token.Text is "(" or "{")
            {
                if (depth++ == 0)
                    continue;
            }
            else if (token.Kind == Kind.Symbol && token.Text is ")" or "}")
            {
                if (--depth == 0)
                    return (args, i);
            }
            else if (depth == 1 && token.Kind == Kind.Symbol && token.Text == ",")
            {
                args.Add([]);
                continue;
            }

            args[^1].Add(token);
        }

        return (args, tokens.Count - 1);
    }

    private static bool HasTableConstructor(List<Token> tokens, List<(int Start, int End)> dataTableSpans)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Symbol || tokens[i].Text != "{")
                continue;
            if (i > 0 && tokens[i - 1].Kind == Kind.Word && tokens[i - 1].Text.Equals("IN", StringComparison.OrdinalIgnoreCase))
                continue;
            if (dataTableSpans.Any(span => i > span.Start && i < span.End))
                continue;
            return true;
        }

        return false;
    }

    private static bool IsValueColumn(string name)
        => name.StartsWith("Value", StringComparison.OrdinalIgnoreCase)
           && name[5..].All(char.IsAsciiDigit);
}
