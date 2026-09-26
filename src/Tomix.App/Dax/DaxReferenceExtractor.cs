namespace Tomix.App.Dax;

/// <summary>How a reference is written in the expression, which decides how it can be resolved.</summary>
public enum DaxReferenceShape
{
    /// <summary>A table-qualified column/measure reference: <c>'Table'[X]</c> or <c>Table[X]</c>.</summary>
    Qualified,

    /// <summary>A lone <c>[X]</c>: a measure, or a column of the expression's own table.</summary>
    Unqualified,

    /// <summary>A quoted table with no bracket: <c>'Table'</c> — always a table in DAX.</summary>
    Table,

    /// <summary>
    /// A bare word that may be a table (<c>COUNTROWS(Sales)</c>) — but equally a VAR or keyword.
    /// Only count it when the model actually has a table by this name.
    /// </summary>
    TableCandidate,

    /// <summary>
    /// A bare or dotted word followed by <c>(</c>: <c>AddTax(</c>, <c>Local.AddTax(</c>. A built-in
    /// function or a user-defined function (UDF) — only count it when the model has a function
    /// by this name. <see cref="DaxReferenceExtractor.DaxReference.Object"/> holds the full
    /// (dotted) name; the span covers the name, not the parenthesis.
    /// </summary>
    FunctionCall,
}

/// <summary>
/// Extracts column/measure/table references from a DAX expression, with the exact character span
/// each reference occupies so a rename can splice-rewrite it in place. Shared by dependency
/// analysis (<c>deps</c>) and the rename reference check so the recognition lives in one place.
/// Lexer-based (see <see cref="DaxTokenizer"/>): references inside string literals and comments
/// are never reported, and escaped names (<c>''</c>/<c>]]</c>) are unescaped.
/// </summary>
public static class DaxReferenceExtractor
{
    /// <summary>A reference found in a DAX expression.</summary>
    /// <param name="Shape">How the reference is written (decides resolution and rewrite form).</param>
    /// <param name="Table">The table name; <c>null</c> for <see cref="DaxReferenceShape.Unqualified"/> and <see cref="DaxReferenceShape.FunctionCall"/>.</param>
    /// <param name="Object">The bracketed name, or the called function's name; <c>null</c> for table-only shapes.</param>
    /// <param name="Start">Inclusive offset of the reference's first character in the expression.</param>
    /// <param name="End">Inclusive offset of the reference's last character in the expression.</param>
    public readonly record struct DaxReference(
        DaxReferenceShape Shape, string? Table, string? Object, int Start, int End)
    {
        public bool FullyQualified => Shape == DaxReferenceShape.Qualified;
    }

    /// <param name="expression">The DAX text.</param>
    /// <param name="includeFunctionCalls">
    /// Also report <see cref="DaxReferenceShape.FunctionCall"/> references. Off by default: most
    /// calls are built-ins, so only consumers that resolve against model functions opt in.
    /// </param>
    public static IReadOnlyList<DaxReference> Extract(string? expression, bool includeFunctionCalls = false)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var tokens = DaxTokenizer.Tokenize(expression);
        var variables = DeclaredVariables(tokens);
        var references = new List<DaxReference>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;

            // A word followed by '(' is a function, never a table.
            if (token.Kind == DaxTokenKind.Identifier && FunctionCallEnd(tokens, i) is { } last)
            {
                if (includeFunctionCalls)
                    references.Add(new DaxReference(
                        DaxReferenceShape.FunctionCall, null, FunctionName(tokens, i, last),
                        token.Start, tokens[last].End));
                i = last;
                continue;
            }

            switch (token.Kind)
            {
                case DaxTokenKind.QuotedTable when next.Kind == DaxTokenKind.BracketName:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Qualified, token.Text, next.Text, token.Start, next.End));
                    i++;
                    break;

                case DaxTokenKind.QuotedTable:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Table, token.Text, null, token.Start, token.End));
                    break;

                // Keywords and VAR names never qualify a bracket ("RETURN [Total]" is the keyword
                // followed by an unqualified measure, not table "RETURN"); reserved words must be
                // quoted to name a table. On a failed guard the identifier falls to the case below
                // and the bracket is reported as Unqualified on the next iteration.
                case DaxTokenKind.Identifier when next.Kind == DaxTokenKind.BracketName
                    && !Keywords.Contains(token.Text)
                    && !variables.Contains(token.Text):
                    references.Add(new DaxReference(
                        DaxReferenceShape.Qualified, token.Text, next.Text, token.Start, next.End));
                    i++;
                    break;

                case DaxTokenKind.Identifier:
                    // A VAR name or keyword is not a table (function calls were taken above).
                    if (variables.Contains(token.Text) || Keywords.Contains(token.Text))
                        break;

                    references.Add(new DaxReference(
                        DaxReferenceShape.TableCandidate, token.Text, null, token.Start, token.End));
                    break;

                case DaxTokenKind.BracketName:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Unqualified, null, token.Text, token.Start, token.End));
                    break;
            }
        }

        return references;
    }

    /// <summary>
    /// When the identifier at <paramref name="first"/> starts a call — a name, or an unbroken
    /// dotted chain (<c>Ns.Func</c>, no whitespace around the dots), followed by <c>(</c> —
    /// returns the index of the chain's last identifier; otherwise <c>null</c>. Whitespace
    /// before the parenthesis is allowed (<c>AddTax ( x )</c>).
    /// </summary>
    private static int? FunctionCallEnd(List<DaxToken> tokens, int first)
    {
        var last = first;
        while (last + 2 < tokens.Count
               && tokens[last + 1] is { Kind: DaxTokenKind.Symbol, Text: "." } dot
               && tokens[last + 2].Kind == DaxTokenKind.Identifier
               && dot.Start == tokens[last].End + 1
               && tokens[last + 2].Start == dot.End + 1)
            last += 2;

        return last + 1 < tokens.Count && tokens[last + 1] is { Kind: DaxTokenKind.Symbol, Text: "(" }
            ? last
            : null;
    }

    private static string FunctionName(List<DaxToken> tokens, int first, int last)
        => string.Join('.', tokens.Skip(first).Take(last - first + 1)
            .Where(t => t.Kind == DaxTokenKind.Identifier)
            .Select(t => t.Text));

    /// <summary>
    /// Names declared with <c>VAR</c> anywhere in the expression. Collected up front so a bare
    /// word matching a VAR is never reported as a table candidate — a rare table-shadowed-by-VAR
    /// false negative is safer than a VAR-reported-as-table false positive.
    /// </summary>
    private static HashSet<string> DeclaredVariables(List<DaxToken> tokens)
    {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind == DaxTokenKind.Identifier
                && tokens[i].Text.Equals("VAR", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Kind == DaxTokenKind.Identifier)
                variables.Add(tokens[i + 1].Text);
        }

        return variables;
    }

    // Bare words that are DAX syntax, not object names. For TableCandidate a missing entry merely
    // risks a candidate that no table matches — resolution drops it anyway. For the qualified
    // pairing a missing entry would mis-qualify a following bracket (keyword[X] instead of a lone
    // [X]), so keep operator/statement keywords that can precede a reference in this list.
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "RETURN", "EVALUATE", "DEFINE", "MEASURE", "COLUMN", "TABLE",
        "ORDER", "BY", "ASC", "DESC", "START", "AT", "IN", "NOT", "TRUE", "FALSE",
    };
}
