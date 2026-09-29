namespace Tomix.App.Dax;

/// <summary>
/// Infers the column names a calculated table's DAX produces, for models whose TMDL carries no
/// column declarations because the table was never evaluated (hand-written or generated TMDL).
/// Only shapes whose output columns are spelled out in the expression itself are recognized —
/// <c>DATATABLE("Name", TYPE, ..., {rows})</c> and <c>ROW("Name", expr, ...)</c>; anything else
/// returns null, meaning the columns are unknown offline.
/// </summary>
internal static class DaxCalculatedTableColumns
{
    private enum Kind { String, Word, Symbol }

    private readonly record struct Token(Kind Kind, string Text);

    public static IReadOnlyList<string>? Infer(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var tokens = Tokenize(expression);
        if (tokens.Count < 3 || tokens[0].Kind != Kind.Word || tokens[1].Text != "(")
            return null;

        var args = TopLevelArguments(tokens);
        if (args is null)
            return null;

        return tokens[0].Text.ToUpperInvariant() switch
        {
            // DATATABLE: name/type pairs, then the {rows} literal as the last argument.
            "DATATABLE" when args.Count >= 3 && args.Count % 2 == 1 => Names(args[..^1]),
            // ROW: name/expression pairs.
            "ROW" when args.Count >= 2 && args.Count % 2 == 0 => Names(args),
            _ => null
        };
    }

    /// <summary>The string literal at every even argument position, or null if any is not one.</summary>
    private static List<string>? Names(List<List<Token>> args)
    {
        var names = new List<string>();
        for (var i = 0; i < args.Count; i += 2)
        {
            if (args[i] is not [{ Kind: Kind.String } name])
                return null;
            names.Add(name.Text);
        }

        return names;
    }

    /// <summary>
    /// Splits the arguments of the call that opens at tokens[1]; null unless that call spans the
    /// whole expression (a wrapped or composed expression's columns are not the call's).
    /// </summary>
    private static List<List<Token>>? TopLevelArguments(List<Token> tokens)
    {
        var args = new List<List<Token>> { new() };
        var depth = 0;

        for (var i = 1; i < tokens.Count; i++)
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
                    return i == tokens.Count - 1 ? args : null;
            }
            else if (depth == 1 && token.Kind == Kind.Symbol && token.Text == ",")
            {
                args.Add([]);
                continue;
            }

            args[^1].Add(token);
        }

        return null;
    }

    /// <summary>Strings (unescaped), words, and single-character symbols; whitespace and comments dropped.</summary>
    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];
            var next = i + 1 < expression.Length ? expression[i + 1] : '\0';

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                var end = expression.IndexOf('\n', i);
                i = end < 0 ? expression.Length : end + 1;
            }
            else if (c == '/' && next == '*')
            {
                var end = expression.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? expression.Length : end + 2;
            }
            else if (c == '"')
            {
                tokens.Add(new Token(Kind.String, ReadDelimited(expression, ref i, '"')));
            }
            // 'Table' and [Column] are opaque names; a comma inside one is not a separator.
            else if (c == '\'')
            {
                tokens.Add(new Token(Kind.Word, ReadDelimited(expression, ref i, '\'')));
            }
            else if (c == '[')
            {
                tokens.Add(new Token(Kind.Word, ReadDelimited(expression, ref i, ']')));
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or '.'))
                    i++;
                tokens.Add(new Token(Kind.Word, expression[start..i]));
            }
            else
            {
                tokens.Add(new Token(Kind.Symbol, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>Reads past a delimited literal, honoring the doubled-closer escape; returns its text.</summary>
    private static string ReadDelimited(string expression, ref int i, char close)
    {
        var text = new System.Text.StringBuilder();
        i++;
        while (i < expression.Length)
        {
            if (expression[i] == close)
            {
                if (i + 1 < expression.Length && expression[i + 1] == close)
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            text.Append(expression[i++]);
        }

        return text.ToString();
    }
}
