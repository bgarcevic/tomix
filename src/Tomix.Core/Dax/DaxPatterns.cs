using Tomix.Core.Dax.Engine;

namespace Tomix.Core.Dax;

/// <summary>
/// Lint checks that read the parsed DAX rather than its text, so a pattern inside a string or a
/// comment never matches. Model-agnostic: each check sees one expression. An expression with a
/// syntax error reports nothing; <c>tx validate</c> owns broken expressions.
/// </summary>
public static class DaxPatterns
{
    /// <summary>
    /// The names of <c>VAR</c>s that no later variable and no <c>RETURN</c> of their block reads,
    /// in source order. A name that a nested block declares again counts as read, so shadowing
    /// can hide an unused outer variable but never invents one.
    /// </summary>
    public static IReadOnlyList<string> UnusedVariables(string? dax)
    {
        if (!TryParse(dax, out var script))
            return [];

        var unused = new List<string>();
        foreach (var block in Descendants(script).OfType<DaxVarReturn>())
        {
            for (var i = 0; i < block.Variables.Count; i++)
            {
                var name = block.Variables[i].Name.Text;
                var scope = block.Variables.Skip(i + 1).Select(v => v.Value)
                    .Concat(block.Body is null ? [] : [block.Body]);
                if (!scope.SelectMany(Descendants).Any(node => Names(node, name)))
                    unused.Add(name);
            }
        }

        return unused;
    }

    /// <summary>
    /// The offsets of <c>CALCULATE</c>/<c>CALCULATETABLE</c> calls that have no filter arguments
    /// and so change nothing. Two shapes are certain: <c>CALCULATE([Measure])</c> anywhere, since a
    /// measure reference already does the context transition (a lone column there fails, so the
    /// reference is a measure); and a one-argument call outside every function call, where no row
    /// context can exist to transition from. The second assumes the expression starts without a
    /// row context, so it holds for measures and calculation items but not calculated columns.
    /// </summary>
    public static IReadOnlyList<int> RedundantCalculates(string? dax)
    {
        if (!TryParse(dax, out var script))
            return [];

        var found = new List<int>();
        Visit(script, insideCall: false);
        return found;

        void Visit(DaxNode node, bool insideCall)
        {
            if (node is DaxCall { Arguments: [var only] } call && IsCalculate(call.FunctionName))
            {
                var measure = call.FunctionName.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
                              && Unwrap(only) is DaxReference { Tokens: [.., { Kind: DaxTokenKind.ColumnReference }] };
                if (measure || !insideCall)
                    found.Add(call.Callee is DaxLeaf callee ? callee.Token.Start : call.OpenParenthesis.Start);
            }

            var nested = insideCall || node is DaxCall;
            foreach (var child in Children(node))
                Visit(child, nested);
        }
    }

    private static bool IsCalculate(string name)
        => name.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CALCULATETABLE", StringComparison.OrdinalIgnoreCase);

    private static DaxNode Unwrap(DaxNode node)
        => node is DaxBracketed { IsBrace: false, Items: [var single] } ? Unwrap(single) : node;

    private static bool TryParse(string? dax, out DaxScript script)
    {
        script = null!;
        if (string.IsNullOrWhiteSpace(dax) || DaxSyntaxCheck.Analyze(dax).Count > 0)
            return false;

        script = DaxParser.Parse(dax);
        return true;
    }

    /// <summary>Whether <paramref name="node"/> is a bare identifier or a table-qualified reference that names <paramref name="name"/>.</summary>
    private static bool Names(DaxNode node, string name) => node switch
    {
        DaxLeaf { Token.Kind: DaxTokenKind.Identifier } leaf => Same(leaf.Token, name),
        DaxReference { Tokens: [{ Kind: DaxTokenKind.Identifier } first, ..] } => Same(first, name),
        _ => false,
    };

    private static bool Same(DaxToken token, string name)
        => token.Text.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DaxNode> Descendants(DaxNode node)
    {
        yield return node;
        foreach (var child in Children(node))
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private static IEnumerable<DaxNode> Children(DaxNode node) => node switch
    {
        DaxCall call => [call.Callee, .. call.Arguments],
        DaxBracketed bracketed => bracketed.Items,
        DaxBinary binary => [binary.Left, binary.Right],
        DaxUnary unary => [unary.Operand],
        DaxSuffixed suffixed => [suffixed.Expression],
        DaxVarReturn block => [.. block.Variables.Select(v => v.Value), .. block.Body is null ? [] : new[] { block.Body }],
        DaxDefinition { Value: { } value } => [value],
        DaxFunctionDefinition function => [.. function.Parameters, .. function.Body is null ? [] : new[] { function.Body }],
        DaxDefine define => define.Definitions,
        DaxEvaluate evaluate => [.. evaluate.Expression is null ? [] : new[] { evaluate.Expression }, .. evaluate.Clauses.SelectMany(c => c.Items)],
        DaxStatement statement => [statement.Body],
        DaxScript script => script.Statements,
        _ => [],
    };
}
