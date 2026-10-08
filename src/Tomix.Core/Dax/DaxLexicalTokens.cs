using System.Text;

namespace Tomix.Core.Dax;

/// <summary>The lexical kind of a <see cref="DaxLexeme"/>, reduced to what reference analysis needs.</summary>
public enum DaxLexemeKind
{
    /// <summary>A bare word, including an unbroken dotted chain: <c>Sales</c>, <c>NORM.DIST</c>, <c>Local.AddTax</c>.</summary>
    Identifier,

    /// <summary>A single-quoted table name: <c>'Order Lines'</c>.</summary>
    QuotedTable,

    /// <summary>A bracketed column or measure name: <c>[Sales Amount]</c>.</summary>
    ColumnReference,

    /// <summary>A string literal: <c>"text"</c>.</summary>
    String,

    /// <summary>A date-time literal: <c>dt"2024-01-01"</c>.</summary>
    DateTime,

    /// <summary>A number literal: <c>1</c>, <c>1.5E+10</c>.</summary>
    Number,

    /// <summary>A query parameter: <c>@Risk</c>.</summary>
    QueryParameter,

    /// <summary>An opening parenthesis.</summary>
    OpenParenthesis,

    /// <summary>A dot that is not part of an identifier, as in <c>'Date'.[Date]</c>.</summary>
    Dot,

    /// <summary>Any other token: operators, separators, braces, unknown characters.</summary>
    Other,
}

/// <summary>
/// One token of DAX source. <see cref="Source"/> is the text exactly as written;
/// <see cref="Name"/> is the unescaped contents of a quoted table, bracketed name or string
/// (<c>''</c>, <c>]]</c>, <c>""</c> collapse to one character), and equals <see cref="Source"/>
/// for every other kind.
/// </summary>
/// <param name="Start">Offset of the token's first character, including any delimiter.</param>
/// <param name="Length">Length of the token in the source, including any delimiters.</param>
public readonly record struct DaxLexeme(DaxLexemeKind Kind, string Source, string Name, int Start, int Length)
{
    /// <summary>Inclusive offset of the token's last character.</summary>
    public int End => Start + Length - 1;
}

/// <summary>
/// The token stream of the DAX language engine, for model-aware analysis outside Core (reference
/// extraction, rename and remove guards). Comments are dropped, and string, date-time, table and
/// column literals are single tokens, so nothing inside them is ever read as a reference.
/// </summary>
public static class DaxLexicalTokens
{
    public static IReadOnlyList<DaxLexeme> Read(string dax)
    {
        var lexemes = new List<DaxLexeme>();
        foreach (var token in Engine.DaxLexer.Tokenize(dax))
        {
            if (token.Kind == Engine.DaxTokenKind.EndOfFile)
                continue;

            // The engine's Text may be case-normalized (sum( → SUM); analysis wants what was written.
            var source = dax.Substring(token.Start, token.Length);
            var kind = KindOf(token.Kind);
            var name = kind switch
            {
                DaxLexemeKind.QuotedTable => Unescape(source, '\''),
                DaxLexemeKind.ColumnReference => Unescape(source, ']'),
                DaxLexemeKind.String => Unescape(source, '"'),
                _ => source,
            };
            lexemes.Add(new DaxLexeme(kind, source, name, token.Start, token.Length));
        }

        return lexemes;
    }

    private static DaxLexemeKind KindOf(Engine.DaxTokenKind kind) => kind switch
    {
        Engine.DaxTokenKind.Identifier => DaxLexemeKind.Identifier,
        Engine.DaxTokenKind.QuotedTable => DaxLexemeKind.QuotedTable,
        Engine.DaxTokenKind.ColumnReference => DaxLexemeKind.ColumnReference,
        Engine.DaxTokenKind.String => DaxLexemeKind.String,
        Engine.DaxTokenKind.DateTime => DaxLexemeKind.DateTime,
        Engine.DaxTokenKind.Number => DaxLexemeKind.Number,
        Engine.DaxTokenKind.QueryParameter => DaxLexemeKind.QueryParameter,
        Engine.DaxTokenKind.OpenParenthesis => DaxLexemeKind.OpenParenthesis,
        Engine.DaxTokenKind.Dot => DaxLexemeKind.Dot,
        _ => DaxLexemeKind.Other,
    };

    /// <summary>
    /// The contents between the opening delimiter and <paramref name="close"/>, with a doubled
    /// closer read as one literal character. An unterminated token keeps everything after the opener.
    /// </summary>
    private static string Unescape(string source, char close)
    {
        var text = new StringBuilder(source.Length);
        for (var i = 1; i < source.Length; i++)
        {
            if (source[i] != close)
            {
                text.Append(source[i]);
                continue;
            }

            if (i + 1 < source.Length && source[i + 1] == close)
            {
                text.Append(close);
                i++;
                continue;
            }

            break; // closing delimiter
        }

        return text.ToString();
    }
}
