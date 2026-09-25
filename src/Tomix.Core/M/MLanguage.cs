namespace Tomix.Core.M;

/// <summary>What a run of Power Query (M) source text is, for syntax highlighting.</summary>
public enum MTextClassification
{
    Text,
    Keyword,

    /// <summary><c>true</c>, <c>false</c>, <c>null</c>, <c>#infinity</c>, <c>#nan</c>.</summary>
    Literal,
    StringLiteral,
    Number,
    Comment,

    /// <summary>A library function or member (<c>Table.AddColumn</c>, <c>Int64.Type</c>) or a <c>#table</c>-style constructor.</summary>
    Function,

    /// <summary>A name being defined: a <c>let</c> step or a record field (<c>Source =</c>, <c>#"Changed Type" =</c>).</summary>
    DefinitionName,

    /// <summary>A field access such as <c>[Amount]</c> or <c>[#"Sales Amount"]</c>, brackets included.</summary>
    FieldAccess,
    Operator,
    Punctuation,
}

/// <summary>A run of M source text and its classification. Spans are ordered and never overlap.</summary>
public readonly record struct MClassifiedSpan(
    int Start,
    int Length,
    MTextClassification Classification);

/// <summary>
/// Lexical classification of Power Query (M) source for highlighting. A single synchronous pass
/// with no parser behind it, so it is cheap enough for every <c>get</c>/<c>ls</c>, and it never
/// throws: broken input (an unterminated string, comment, or quoted identifier) runs to the end
/// of the text. Whitespace is left out of the spans. Formatting and validation use the real
/// parser (<c>Tomix.App.Format.M</c>); this only colors text.
/// </summary>
public static class MLanguage
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "as", "catch", "each", "else", "error", "if", "in", "is", "let", "meta", "not",
        "or", "otherwise", "section", "shared", "then", "try", "type",
    };

    private static readonly HashSet<string> Literals = new(StringComparer.Ordinal) { "true", "false", "null" };

    // Primitive type names are only keywords in a type position (`type text`, `as number`).
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "any", "anynonnull", "binary", "date", "datetime", "datetimezone", "duration", "function",
        "list", "logical", "none", "null", "nullable", "number", "optional", "record", "table",
        "text", "time", "type",
    };

    private static readonly HashSet<string> TypePositionKeywords = new(StringComparer.Ordinal)
    {
        "type", "as", "nullable", "optional",
    };

    private static readonly HashSet<string> HashLiterals = new(StringComparer.Ordinal) { "#infinity", "#nan" };

    private static readonly string[] MultiCharOperators = ["...", "=>", "<=", ">=", "<>", "..", "??"];

    public static IReadOnlyList<MClassifiedSpan> Classify(string m)
    {
        var tokens = Lex(m);
        var spans = new MClassifiedSpan[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            spans[i] = new MClassifiedSpan(tokens[i].Start, tokens[i].Length, Classify(m, tokens, i));
        return spans;
    }

    private enum TokenKind
    {
        Identifier,
        QuotedIdentifier,
        HashKeyword,
        StringLiteral,
        Number,
        Comment,
        FieldAccess,
        Operator,
        Punctuation,
    }

    private readonly record struct Token(int Start, int Length, TokenKind Kind);

    private static MTextClassification Classify(string m, List<Token> tokens, int index)
    {
        var token = tokens[index];
        switch (token.Kind)
        {
            case TokenKind.StringLiteral:
                return MTextClassification.StringLiteral;
            case TokenKind.Number:
                return MTextClassification.Number;
            case TokenKind.Comment:
                return MTextClassification.Comment;
            case TokenKind.FieldAccess:
                return MTextClassification.FieldAccess;
            case TokenKind.Operator:
                return MTextClassification.Operator;
            case TokenKind.Punctuation:
                return MTextClassification.Punctuation;
            case TokenKind.HashKeyword:
                return HashLiterals.Contains(Text(m, token))
                    ? MTextClassification.Literal
                    : MTextClassification.Function;
            case TokenKind.QuotedIdentifier:
                return IsDefinition(m, tokens, index) ? MTextClassification.DefinitionName : MTextClassification.Text;
        }

        var text = Text(m, token);
        var previous = Neighbor(tokens, index, -1);
        if (previous is { Kind: TokenKind.Identifier } p &&
            TypePositionKeywords.Contains(Text(m, p)) &&
            PrimitiveTypes.Contains(text) &&
            (!string.Equals(Text(m, p), "nullable", StringComparison.Ordinal) || IsTypePosition(m, tokens, index - 1)))
            return MTextClassification.Keyword;

        if (Literals.Contains(text))
            return MTextClassification.Literal;
        if (Keywords.Contains(text))
            return MTextClassification.Keyword;
        if (IsDefinition(m, tokens, index))
            return MTextClassification.DefinitionName;

        var next = Neighbor(tokens, index, 1);
        if (text.Contains('.', StringComparison.Ordinal) || next is { Kind: TokenKind.Punctuation } n && m[n.Start] == '(')
            return MTextClassification.Function;

        return MTextClassification.Text;
    }

    // `nullable` is itself only a type keyword after `type`/`as`: `type nullable text`.
    private static bool IsTypePosition(string m, List<Token> tokens, int index)
        => Neighbor(tokens, index, -1) is { Kind: TokenKind.Identifier } previous &&
           TypePositionKeywords.Contains(Text(m, previous));

    /// <summary>
    /// A name followed by <c>=</c> right after <c>let</c>, a comma, or an opening bracket: a let
    /// step or a record field. Comparisons (<c>if a = b</c>, <c>each [x] = 1</c>) never sit there.
    /// </summary>
    private static bool IsDefinition(string m, List<Token> tokens, int index)
    {
        if (Neighbor(tokens, index, 1) is not { Kind: TokenKind.Operator } next || next.Length != 1 || m[next.Start] != '=')
            return false;

        return Neighbor(tokens, index, -1) switch
        {
            { Kind: TokenKind.Punctuation } p => m[p.Start] is ',' or '[',
            { Kind: TokenKind.Identifier } p => string.Equals(Text(m, p), "let", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>The nearest token in <paramref name="direction"/>, skipping comments.</summary>
    private static Token? Neighbor(List<Token> tokens, int index, int direction)
    {
        for (var i = index + direction; i >= 0 && i < tokens.Count; i += direction)
        {
            if (tokens[i].Kind != TokenKind.Comment)
                return tokens[i];
        }

        return null;
    }

    private static string Text(string m, Token token) => m.Substring(token.Start, token.Length);

    private static List<Token> Lex(string m)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < m.Length)
        {
            var c = m[i];
            var start = i;

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && At(m, i + 1, '/'))
            {
                while (i < m.Length && m[i] is not ('\r' or '\n'))
                    i++;
                tokens.Add(new Token(start, i - start, TokenKind.Comment));
            }
            else if (c == '/' && At(m, i + 1, '*'))
            {
                var end = m.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? m.Length : end + 2;
                tokens.Add(new Token(start, i - start, TokenKind.Comment));
            }
            else if (c == '"')
            {
                i = SkipString(m, i);
                tokens.Add(new Token(start, i - start, TokenKind.StringLiteral));
            }
            else if (c == '#' && At(m, i + 1, '"'))
            {
                i = SkipString(m, i + 1);
                tokens.Add(new Token(start, i - start, TokenKind.QuotedIdentifier));
            }
            else if (c == '#' && i + 1 < m.Length && char.IsLetter(m[i + 1]))
            {
                i++;
                while (i < m.Length && char.IsLetter(m[i]))
                    i++;
                tokens.Add(new Token(start, i - start, TokenKind.HashKeyword));
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < m.Length && char.IsDigit(m[i + 1]) && !At(m, i - 1, '.')))
            {
                i = SkipNumber(m, i);
                tokens.Add(new Token(start, i - start, TokenKind.Number));
            }
            else if (IsIdentifierStart(c))
            {
                i++;
                while (i < m.Length && IsIdentifierPart(m[i]))
                    i++;
                // A trailing dot belongs to the next token (`x..y` is a range, not a name).
                while (i - 1 > start && m[i - 1] == '.')
                    i--;
                tokens.Add(new Token(start, i - start, TokenKind.Identifier));
            }
            else if (c == '[' && FieldAccessEnd(m, i) is { } fieldEnd)
            {
                i = fieldEnd;
                tokens.Add(new Token(start, i - start, TokenKind.FieldAccess));
            }
            else if (c is ',' or ';' or '(' or ')' or '{' or '}' or '[' or ']')
            {
                i++;
                tokens.Add(new Token(start, 1, TokenKind.Punctuation));
            }
            else
            {
                var op = Array.Find(MultiCharOperators, o => string.CompareOrdinal(m, i, o, 0, o.Length) == 0);
                i += op?.Length ?? 1;
                tokens.Add(new Token(start, i - start, TokenKind.Operator));
            }
        }

        return tokens;
    }

    private static bool At(string m, int index, char c) => index >= 0 && index < m.Length && m[index] == c;

    /// <summary>Skips a <c>"..."</c> literal starting at <paramref name="quote"/>; <c>""</c> is an escaped quote.</summary>
    private static int SkipString(string m, int quote)
    {
        var i = quote + 1;
        while (i < m.Length)
        {
            if (m[i] == '"')
            {
                if (At(m, i + 1, '"'))
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return m.Length;
    }

    private static int SkipNumber(string m, int i)
    {
        if (m[i] == '0' && i + 1 < m.Length && m[i + 1] is 'x' or 'X')
        {
            i += 2;
            while (i < m.Length && char.IsAsciiHexDigit(m[i]))
                i++;
            return i;
        }

        while (i < m.Length && char.IsDigit(m[i]))
            i++;
        if (At(m, i, '.') && i + 1 < m.Length && char.IsDigit(m[i + 1]))
        {
            i++;
            while (i < m.Length && char.IsDigit(m[i]))
                i++;
        }

        if (i < m.Length && m[i] is 'e' or 'E')
        {
            var exponent = i + 1;
            if (exponent < m.Length && m[exponent] is '+' or '-')
                exponent++;
            if (exponent < m.Length && char.IsDigit(m[exponent]))
            {
                i = exponent;
                while (i < m.Length && char.IsDigit(m[i]))
                    i++;
            }
        }

        return i;
    }

    /// <summary>
    /// The end of a single-field access at <paramref name="open"/> (<c>[Amount]</c>,
    /// <c>[Sales Amount]</c>, <c>[#"Sales Amount"]</c>), or null when the bracket opens a record
    /// or a projection instead.
    /// </summary>
    private static int? FieldAccessEnd(string m, int open)
    {
        var i = open + 1;
        while (i < m.Length && m[i] is ' ' or '\t')
            i++;

        if (At(m, i, '#') && At(m, i + 1, '"'))
        {
            i = SkipString(m, i + 1);
        }
        else
        {
            var nameStart = i;
            while (i < m.Length && (IsIdentifierPart(m[i]) || m[i] is ' ' or '\t'))
                i++;
            if (i == nameStart || !IsIdentifierStart(m[nameStart]))
                return null;
        }

        while (i < m.Length && m[i] is ' ' or '\t')
            i++;
        return At(m, i, ']') ? i + 1 : null;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '.' ||
           char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.ConnectorPunctuation
               or System.Globalization.UnicodeCategory.NonSpacingMark
               or System.Globalization.UnicodeCategory.SpacingCombiningMark
               or System.Globalization.UnicodeCategory.Format;
}
