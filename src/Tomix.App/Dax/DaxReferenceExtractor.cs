using Tomix.Core.Dax;

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
/// A token-pattern pass over the DAX engine's lexer (<see cref="DaxLexicalTokens"/>): references
/// inside string literals and comments are never reported, and escaped names (<c>''</c>/<c>]]</c>)
/// are unescaped. It is not a parser; grammar-level analysis is out of scope (same design as
/// Tabular Editor's lexer-only FormulaFixup).
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

        var tokens = DaxLexicalTokens.Read(expression);
        var variables = DeclaredVariables(tokens);
        var references = new List<DaxReference>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;

            // A word followed by '(' is a function, never a table. The lexer keeps an unbroken
            // dotted chain (Local.AddTax, NORM.DIST) as one identifier, so the name is whole.
            if (token.Kind == DaxLexemeKind.Identifier && next.Kind == DaxLexemeKind.OpenParenthesis)
            {
                if (includeFunctionCalls)
                    references.Add(new DaxReference(
                        DaxReferenceShape.FunctionCall, null, token.Name, token.Start, token.End));
                continue;
            }

            switch (token.Kind)
            {
                case DaxLexemeKind.QuotedTable when next.Kind == DaxLexemeKind.ColumnReference:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Qualified, token.Name, next.Name, token.Start, next.End));
                    i++;
                    break;

                case DaxLexemeKind.QuotedTable:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Table, token.Name, null, token.Start, token.End));
                    break;

                // Keywords and VAR names never qualify a bracket ("RETURN [Total]" is the keyword
                // followed by an unqualified measure, not table "RETURN"); reserved words must be
                // quoted to name a table. On a failed guard the identifier falls to the case below
                // and the bracket is reported as Unqualified on the next iteration.
                case DaxLexemeKind.Identifier when next.Kind == DaxLexemeKind.ColumnReference
                    && !Keywords.Contains(token.Name)
                    && !variables.Contains(token.Name):
                    references.Add(new DaxReference(
                        DaxReferenceShape.Qualified, token.Name, next.Name, token.Start, next.End));
                    i++;
                    break;

                case DaxLexemeKind.Identifier:
                    // A VAR name or keyword is not a table (function calls were taken above).
                    if (variables.Contains(token.Name) || Keywords.Contains(token.Name))
                        break;

                    references.Add(new DaxReference(
                        DaxReferenceShape.TableCandidate, token.Name, null, token.Start, token.End));
                    break;

                case DaxLexemeKind.ColumnReference:
                    references.Add(new DaxReference(
                        DaxReferenceShape.Unqualified, null, token.Name, token.Start, token.End));
                    break;
            }
        }

        return references;
    }

    /// <summary>
    /// Names declared with <c>VAR</c> anywhere in the expression. Collected up front so a bare
    /// word matching a VAR is never reported as a table candidate — a rare table-shadowed-by-VAR
    /// false negative is safer than a VAR-reported-as-table false positive.
    /// </summary>
    private static HashSet<string> DeclaredVariables(IReadOnlyList<DaxLexeme> tokens)
    {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind == DaxLexemeKind.Identifier
                && tokens[i].Name.Equals("VAR", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Kind == DaxLexemeKind.Identifier)
                variables.Add(tokens[i + 1].Name);
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
