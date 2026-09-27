using System.Text.Json;

namespace Tomix.Core.Dax;

/// <summary>The kind of offline syntax problem found in a DAX expression.</summary>
public enum DaxSyntaxErrorKind
{
    /// <summary>A character that starts no DAX token, such as <c>#</c> or <c>?</c>.</summary>
    UnexpectedCharacter,

    /// <summary>A parenthesis or brace that never finds its partner.</summary>
    UnbalancedGroup,

    /// <summary>A string, table name, or column reference whose closing delimiter is missing.</summary>
    UnterminatedLiteral,

    /// <summary>A <c>/* */</c> comment whose <c>*/</c> is missing.</summary>
    UnterminatedComment,

    /// <summary>A token where a comma, a closing bracket, or the end of the expression belongs.</summary>
    UnexpectedToken,

    /// <summary>An operator, bracket, or the end where an operand belongs, as in <c>1 +</c>.</summary>
    MissingOperand,

    /// <summary>A <c>VAR</c> without <c>=</c> or without <c>RETURN</c>.</summary>
    IncompleteVarBlock,
}

/// <summary>
/// One offline syntax problem: where it starts in the source and a human-readable message.
/// Offline analysis cannot validate semantics, so these cover only what the surface syntax
/// itself can disprove.
/// </summary>
public readonly record struct DaxSyntaxIssue(
    int Start,
    int Length,
    DaxSyntaxErrorKind Kind,
    string Message)
{
    /// <summary><c>lex</c> for problems inside a single token, <c>parse</c> for problems between tokens.</summary>
    public string Stage => Kind is DaxSyntaxErrorKind.UnexpectedCharacter
        or DaxSyntaxErrorKind.UnterminatedLiteral
        or DaxSyntaxErrorKind.UnterminatedComment
        ? "lex"
        : "parse";

    /// <summary>The kind as a stable camelCase name, for machine output.</summary>
    public string Code => JsonNamingPolicy.CamelCase.ConvertName(Kind.ToString());
}

/// <summary>
/// Offline DAX syntax checking over a single expression, in two passes. The token pass reports
/// characters that start no token, unterminated literals and block comments, and unbalanced
/// parentheses or braces; it runs over the token stream, so everything inside a string or
/// comment is ignored, and an expression full of quirks (<c>''</c>/<c>]]</c> escapes,
/// <c>dt"…"</c>, omitted arguments) is accepted exactly as DAX accepts it. When the tokens are
/// sound, the parser reports its first grammar error: a missing operand or comma, tokens left
/// after the expression, or an incomplete <c>VAR</c> block. Only the first, because what follows
/// a grammar error is the parser's guess.
/// </summary>
public static class DaxSyntaxCheck
{
    public static IReadOnlyList<DaxSyntaxIssue> Analyze(string dax)
    {
        var issues = new List<DaxSyntaxIssue>();
        var tokens = Engine.DaxLexer.Tokenize(dax);
        // The stack of open groups: the character each opener printed, and its offset.
        var open = new List<(char Symbol, int Start)>();

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case Engine.DaxTokenKind.Unknown when token.Length > 0:
                    issues.Add(new DaxSyntaxIssue(
                        token.Start,
                        token.Length,
                        DaxSyntaxErrorKind.UnexpectedCharacter,
                        $"Unexpected character '{token.Text}'."));
                    break;

                case Engine.DaxTokenKind.String or Engine.DaxTokenKind.DateTime
                    when !IsDelimitedTerminated(dax, token.Start, token.Length, '"'):
                    issues.Add(Unterminated(token, "string literal"));
                    break;

                case Engine.DaxTokenKind.QuotedTable
                    when !IsDelimitedTerminated(dax, token.Start, token.Length, '\''):
                    issues.Add(Unterminated(token, "table name (missing closing apostrophe)"));
                    break;

                case Engine.DaxTokenKind.ColumnReference
                    when !IsDelimitedTerminated(dax, token.Start, token.Length, ']'):
                    issues.Add(Unterminated(token, "column reference (missing closing bracket)"));
                    break;

                case Engine.DaxTokenKind.OpenParenthesis:
                    open.Add(('(', token.Start));
                    break;

                case Engine.DaxTokenKind.OpenBrace:
                    open.Add(('{', token.Start));
                    break;

                case Engine.DaxTokenKind.CloseParenthesis:
                    Close('(', ')', token);
                    break;

                case Engine.DaxTokenKind.CloseBrace:
                    Close('{', '}', token);
                    break;
            }
        }

        foreach (var comment in tokens.SelectMany(token => token.LeadingComments)
                     .Concat(tokens.SelectMany(token => token.TrailingComments)))
        {
            if (comment.IsBlock && !comment.Text.EndsWith("*/", StringComparison.Ordinal))
                issues.Add(new DaxSyntaxIssue(
                    comment.Start,
                    comment.Text.Length,
                    DaxSyntaxErrorKind.UnterminatedComment,
                    "Unterminated block comment."));
        }

        // Whatever is still open when the tokens run out was never closed.
        foreach (var (symbol, start) in open)
            issues.Add(new DaxSyntaxIssue(
                start,
                1,
                DaxSyntaxErrorKind.UnbalancedGroup,
                $"'{symbol}' has no matching closing bracket."));

        // A token problem already explains everything after it; the grammar would only echo it.
        if (issues.Count == 0)
        {
            var errors = new List<Engine.DaxParseError>();
            Engine.DaxParser.Parse(dax, errors);
            if (errors.Count > 0)
                issues.Add(new DaxSyntaxIssue(errors[0].Start, errors[0].Length, Kind(errors[0].Kind), errors[0].Message));
        }

        return issues;

        void Close(char expectedOpen, char closer, Engine.DaxToken closerToken)
        {
            if (open.Count == 0 || open[^1].Symbol != expectedOpen)
            {
                issues.Add(new DaxSyntaxIssue(
                    closerToken.Start,
                    closerToken.Length,
                    DaxSyntaxErrorKind.UnbalancedGroup,
                    $"Closing '{closer}' has no matching opening bracket."));
                return;
            }
            open.RemoveAt(open.Count - 1);
        }
    }

    private static DaxSyntaxErrorKind Kind(Engine.DaxParseErrorKind kind) => kind switch
    {
        Engine.DaxParseErrorKind.MissingOperand => DaxSyntaxErrorKind.MissingOperand,
        Engine.DaxParseErrorKind.IncompleteVarBlock => DaxSyntaxErrorKind.IncompleteVarBlock,
        _ => DaxSyntaxErrorKind.UnexpectedToken
    };

    private static DaxSyntaxIssue Unterminated(Engine.DaxToken token, string what) =>
        new(token.Start, token.Length, DaxSyntaxErrorKind.UnterminatedLiteral, $"Unterminated {what}.");

    /// <summary>
    /// Re-walks the delimited run of <paramref name="dax"/> exactly as the lexer does and reports
    /// whether it ended on a real closing delimiter rather than on the end of the input. Doubled
    /// delimiters are escapes, not the end.
    /// </summary>
    private static bool IsDelimitedTerminated(string dax, int start, int length, char delimiter)
    {
        var index = start + 1;
        var end = start + length;
        while (index < end)
        {
            if (dax[index] != delimiter)
            {
                index++;
                continue;
            }
            if (index + 1 < end && dax[index + 1] == delimiter)
            {
                index += 2;
                continue;
            }
            return true;
        }
        return false;
    }
}
