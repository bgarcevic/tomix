namespace Tomix.App.Dax;

/// <summary>The lexical shape of a token relevant to reference extraction.</summary>
internal enum DaxTokenKind
{
    /// <summary>A single-quoted table name, e.g. <c>'Order Lines'</c> (<c>''</c> = literal apostrophe).</summary>
    QuotedTable,

    /// <summary>A bracketed column/measure name, e.g. <c>[Sales Amount]</c> (<c>]]</c> = literal bracket).</summary>
    BracketName,

    /// <summary>A bare word: table name, VAR, function, or keyword — context decides.</summary>
    Identifier,

    /// <summary>Any other single significant character (operators, parentheses, commas, ...).</summary>
    Symbol,
}

/// <summary>
/// A token with its exact character span in the source expression. <see cref="Text"/> is the
/// unescaped name for <see cref="DaxTokenKind.QuotedTable"/>/<see cref="DaxTokenKind.BracketName"/>;
/// <see cref="Start"/>/<see cref="End"/> are inclusive offsets into the raw (untouched) expression,
/// including the surrounding quotes/brackets, so a rewrite can splice the full reference.
/// </summary>
internal readonly record struct DaxToken(DaxTokenKind Kind, string Text, int Start, int End);

/// <summary>
/// A minimal DAX lexer for reference tracking: it classifies quoted tables, bracketed names, and
/// bare identifiers while skipping string literals and comments — the regions where a regex-based
/// scan produces false references. It is not a parser; grammar-level analysis is out of scope
/// (same design as Tabular Editor's lexer-only FormulaFixup).
/// </summary>
internal static class DaxTokenizer
{
    public static List<DaxToken> Tokenize(string expression)
    {
        var tokens = new List<DaxToken>();
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '"')
            {
                i = SkipDelimited(expression, i, '"');
            }
            else if (c == '/' && Peek(expression, i + 1) == '/')
            {
                i = SkipLine(expression, i);
            }
            else if (c == '-' && Peek(expression, i + 1) == '-')
            {
                i = SkipLine(expression, i);
            }
            else if (c == '/' && Peek(expression, i + 1) == '*')
            {
                var end = expression.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? expression.Length : end + 2;
            }
            else if (c == '\'')
            {
                tokens.Add(ReadDelimited(expression, ref i, '\'', DaxTokenKind.QuotedTable));
            }
            else if (c == '[')
            {
                tokens.Add(ReadDelimited(expression, ref i, ']', DaxTokenKind.BracketName));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                    i++;
                tokens.Add(new DaxToken(DaxTokenKind.Identifier, expression[start..i], start, i - 1));
            }
            else if (char.IsDigit(c))
            {
                // A number literal; consume so "1e5" never sheds an identifier-looking "e5".
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '.'))
                    i++;
            }
            else
            {
                tokens.Add(new DaxToken(DaxTokenKind.Symbol, c.ToString(), i, i));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Reads a delimited name starting at <paramref name="i"/>, honoring the doubled-delimiter
    /// escape (<c>''</c> / <c>]]</c>). An unterminated name consumes to end of input.
    /// </summary>
    private static DaxToken ReadDelimited(string expression, ref int i, char close, DaxTokenKind kind)
    {
        var start = i;
        i++; // opening delimiter
        var text = new System.Text.StringBuilder();

        while (i < expression.Length)
        {
            if (expression[i] == close)
            {
                // A doubled closer ('' / ]]) is a literal; a single closer ends the token.
                if (Peek(expression, i + 1) == close)
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }

                i++; // closing delimiter
                return new DaxToken(kind, text.ToString(), start, i - 1);
            }

            text.Append(expression[i]);
            i++;
        }

        return new DaxToken(kind, text.ToString(), start, expression.Length - 1);
    }

    /// <summary>Skips a delimited literal we do not tokenize (string), honoring doubled escapes.</summary>
    private static int SkipDelimited(string expression, int i, char close)
    {
        i++;
        while (i < expression.Length)
        {
            if (expression[i] == close)
            {
                if (Peek(expression, i + 1) == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return i;
    }

    private static int SkipLine(string expression, int i)
    {
        var end = expression.IndexOf('\n', i);
        return end < 0 ? expression.Length : end + 1;
    }

    private static char Peek(string expression, int i)
        => i < expression.Length ? expression[i] : '\0';
}
