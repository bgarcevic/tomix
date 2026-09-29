using Tomix.Core.Dax.Engine;

namespace Tomix.Core.Dax;

/// <summary>How an unqualified <c>[Name]</c> relates to the columns its own expression builds.</summary>
public enum DaxQueryColumnScope
{
    /// <summary>The expression defines no column of that name; resolve it against the model.</summary>
    NotDefined,

    /// <summary>A column the expression builds, used where a row of that table is in context.</summary>
    InScope,

    /// <summary>A column the expression builds, used outside every table that has it.</summary>
    OutOfScope,
}

/// <summary>
/// Scope analysis for query-scoped columns — the columns an expression builds for itself with
/// <c>ADDCOLUMNS</c>, <c>SELECTCOLUMNS</c>, <c>SUMMARIZE</c>, <c>SUMMARIZECOLUMNS</c>,
/// <c>GROUPBY</c>, <c>ROW</c>, <c>DATATABLE</c>, <c>GENERATESERIES</c> (<c>[Value]</c>) and
/// table constructors (<c>[Value]</c>, <c>[Value1]</c>, ...). Runs over the parse tree: an
/// iterator (<c>FILTER</c>, <c>SUMX</c>, <c>ADDCOLUMNS</c>, <c>TOPN</c>, ...) puts its table's
/// columns in scope for its row-context arguments, a <c>VAR</c> holding a table carries that
/// table's columns to where it is iterated, and a column is in scope only inside them.
/// </summary>
/// <remarks>
/// Deliberately lenient where it cannot be sure: a function it has no rule for passes on the
/// columns of every table argument, and every argument after an iterator's table sees the row
/// context. It never makes a reference out of scope that DAX would accept, only the reverse.
/// </remarks>
public static class DaxQueryColumns
{
    /// <summary>
    /// Classifies each unqualified <c>[Name]</c> in <paramref name="dax"/>, keyed by the offset of
    /// its opening bracket. References not in the map are <see cref="DaxQueryColumnScope.NotDefined"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, DaxQueryColumnScope> Analyze(string dax)
    {
        var script = DaxParser.Parse(dax);
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectDefined(script, defined);

        var result = new Dictionary<int, DaxQueryColumnScope>();
        if (defined.Count > 0)
            new Walker(defined, result).Visit(script, Empty, Variables.Empty);
        return result;
    }

    /// <summary>
    /// The columns a calculated table's DAX builds when they are fully spelled out in the
    /// expression — a top-level <c>DATATABLE</c>, <c>ROW</c>, <c>GENERATESERIES</c> or table
    /// constructor; null otherwise (columns that come from model tables are not knowable here).
    /// </summary>
    public static IReadOnlyList<string>? CalculatedTableColumns(string? dax)
    {
        if (string.IsNullOrWhiteSpace(dax))
            return null;

        var errors = new List<DaxParseError>();
        var script = DaxParser.Parse(dax, errors);
        if (errors.Count > 0 || script.Statements is not [DaxStatement { Body: var body }])
            return null;

        return body switch
        {
            DaxCall { FunctionName: var f } when f.Equals("GENERATESERIES", StringComparison.OrdinalIgnoreCase)
                => ["Value"],
            // Every name position must be a literal, or the list would be partial.
            DaxCall { FunctionName: var f } call when f.Equals("ROW", StringComparison.OrdinalIgnoreCase)
                && call.Arguments.Count >= 2 && call.Arguments.Count % 2 == 0
                && NewColumns(call).Count == call.Arguments.Count / 2
                => [.. NewColumns(call)],
            DaxCall { FunctionName: var f } call when f.Equals("DATATABLE", StringComparison.OrdinalIgnoreCase)
                && call.Arguments.Count >= 3 && call.Arguments.Count % 2 == 1
                && NewColumns(call).Count == call.Arguments.Count / 2
                => [.. NewColumns(call)],
            DaxBracketed { IsBrace: true } => [.. Columns(body, Variables.Empty)],
            _ => null
        };
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    /// <summary>
    /// Iterators and the index of the table they iterate; every later argument is evaluated in
    /// that table's row context.
    /// </summary>
    private static readonly Dictionary<string, int> Iterators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FILTER"] = 0,
        ["SUMX"] = 0,
        ["AVERAGEX"] = 0,
        ["MINX"] = 0,
        ["MAXX"] = 0,
        ["COUNTX"] = 0,
        ["COUNTAX"] = 0,
        ["PRODUCTX"] = 0,
        ["CONCATENATEX"] = 0,
        ["RANKX"] = 0,
        ["MEDIANX"] = 0,
        ["PERCENTILEX.INC"] = 0,
        ["PERCENTILEX.EXC"] = 0,
        ["GEOMEANX"] = 0,
        ["STDEVX.P"] = 0,
        ["STDEVX.S"] = 0,
        ["VARX.P"] = 0,
        ["VARX.S"] = 0,
        ["ADDCOLUMNS"] = 0,
        ["SELECTCOLUMNS"] = 0,
        ["SUMMARIZE"] = 0,
        ["GROUPBY"] = 0,
        ["GENERATE"] = 0,
        ["GENERATEALL"] = 0,
        ["FIRSTNONBLANK"] = 0,
        ["LASTNONBLANK"] = 0,
        ["FIRSTNONBLANKVALUE"] = 0,
        ["LASTNONBLANKVALUE"] = 0,
        ["TOPN"] = 1,
        ["SAMPLE"] = 1,
    };

    /// <summary>Every name any table in the expression builds, wherever it is used.</summary>
    private static void CollectDefined(DaxNode node, HashSet<string> defined)
    {
        switch (node)
        {
            case DaxCall call:
                defined.UnionWith(NewColumns(call));
                if (call.FunctionName.Equals("GENERATESERIES", StringComparison.OrdinalIgnoreCase))
                    defined.Add("Value");
                // DATATABLE's { ... } holds its rows, not a table constructor.
                if (call.FunctionName.Equals("DATATABLE", StringComparison.OrdinalIgnoreCase))
                    return;
                break;

            // IN { ... } is a list of values to compare with, not a table anyone iterates.
            case DaxBinary { Operator: var op, Right: DaxBracketed { IsBrace: true } list } binary when op.IsKeyword("IN"):
                CollectDefined(binary.Left, defined);
                foreach (var item in list.Items)
                    CollectDefined(item, defined);
                return;

            case DaxBracketed { IsBrace: true } braces:
                defined.UnionWith(ConstructorColumns(braces));
                break;
        }

        foreach (var child in Children(node))
            CollectDefined(child, defined);
    }

    /// <summary>The query-scoped columns a table expression produces.</summary>
    private static IReadOnlySet<string> Columns(DaxNode node, Variables variables)
    {
        switch (node)
        {
            case DaxCall call:
                var function = call.FunctionName.ToUpperInvariant();
                var own = NewColumns(call);
                switch (function)
                {
                    case "GENERATESERIES":
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Value" };
                    case "ROW" or "DATATABLE" or "SUMMARIZECOLUMNS":
                        return own;
                    // SELECTCOLUMNS keeps only what it lists: new names and bare [Column]s.
                    case "SELECTCOLUMNS":
                        var selected = new HashSet<string>(own, StringComparer.OrdinalIgnoreCase);
                        foreach (var argument in call.Arguments.Skip(1))
                            if (BareColumn(argument) is { } name)
                                selected.Add(name);
                        return selected;
                    case "ADDCOLUMNS" or "SUMMARIZE" or "GROUPBY":
                        return call.Arguments.Count == 0
                            ? own
                            : Union(Columns(call.Arguments[0], variables), own);
                    // Unknown or pass-through (FILTER, TOPN, UNION, CALCULATETABLE, ...): lenient.
                    default:
                        return call.Arguments.Aggregate(Empty, (acc, a) => Union(acc, Columns(a, variables)));
                }

            case DaxBracketed { IsBrace: true } braces:
                return ConstructorColumns(braces);

            case DaxBracketed { Items: [var single] }:
                return Columns(single, variables);

            case DaxLeaf { Token.Kind: DaxTokenKind.Identifier } leaf:
                return variables.Lookup(leaf.Token.Text);

            case DaxVarReturn block:
                var scoped = variables;
                foreach (var variable in block.Variables)
                    scoped = scoped.With(variable.Name.Text, Columns(variable.Value, scoped));
                return block.Body is null ? Empty : Columns(block.Body, scoped);

            default:
                return Empty;
        }
    }

    /// <summary>The column names a call writes as string literals in its name positions.</summary>
    private static HashSet<string> NewColumns(DaxCall call)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var args = call.Arguments;

        switch (call.FunctionName.ToUpperInvariant())
        {
            case "ADDCOLUMNS" or "SELECTCOLUMNS":
                for (var i = 1; i < args.Count; i += 2)
                    AddLiteral(args[i], names);
                break;
            case "ROW":
                for (var i = 0; i < args.Count; i += 2)
                    AddLiteral(args[i], names);
                break;
            case "DATATABLE":
                for (var i = 0; i < args.Count - 1; i += 2)
                    AddLiteral(args[i], names);
                break;
            // Name/expression pairs start at the first string literal (after the group-by columns).
            case "SUMMARIZE" or "SUMMARIZECOLUMNS" or "GROUPBY":
                var first = -1;
                for (var i = 0; i < args.Count; i++)
                    if (Literal(args[i]) is not null) { first = i; break; }
                if (first >= 0)
                    for (var i = first; i < args.Count - 1; i += 2)
                        AddLiteral(args[i], names);
                break;
        }

        return names;
    }

    /// <summary><c>{ 1, 2 }</c> has [Value]; <c>{ (1, "a") }</c> has [Value1], [Value2].</summary>
    private static HashSet<string> ConstructorColumns(DaxBracketed braces)
    {
        var width = braces.Items
            .Select(item => item is DaxBracketed { IsBrace: false, Items.Count: > 1 } tuple ? tuple.Items.Count : 1)
            .DefaultIfEmpty(1)
            .Max();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (width == 1)
            names.Add("Value");
        else
            for (var i = 1; i <= width; i++)
                names.Add($"Value{i}");
        return names;
    }

    private static void AddLiteral(DaxNode node, HashSet<string> names)
    {
        if (Literal(node) is { } name)
            names.Add(name);
    }

    private static string? Literal(DaxNode node)
        => node is DaxLeaf { Token: { Kind: DaxTokenKind.String, Text: var text } } && text.Length >= 2
            ? text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : null;

    private static string? BareColumn(DaxNode node)
        => node is DaxReference { Tokens: [{ Kind: DaxTokenKind.ColumnReference } token] }
            ? Unbracket(token.Text)
            : null;

    private static string Unbracket(string text)
        => (text.EndsWith(']') ? text[1..^1] : text[1..]).Replace("]]", "]", StringComparison.Ordinal);

    private static IReadOnlySet<string> Union(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (right.Count == 0) return left;
        if (left.Count == 0) return right;
        var union = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(right);
        return union;
    }

    private static IEnumerable<DaxNode> Children(DaxNode node) => node switch
    {
        DaxScript script => script.Statements,
        DaxStatement statement => [statement.Body],
        DaxCall call => call.Arguments,
        DaxBracketed bracketed => bracketed.Items,
        DaxBinary binary => [binary.Left, binary.Right],
        DaxUnary unary => [unary.Operand],
        DaxSuffixed suffixed => [suffixed.Expression],
        DaxVarReturn block => block.Variables.Select(v => v.Value)
            .Append(block.Body).OfType<DaxNode>(),
        DaxDefinition { Value: { } value } => [value],
        DaxFunctionDefinition { Body: { } body } => [body],
        DaxDefine define => define.Definitions,
        DaxEvaluate evaluate => evaluate.Clauses.SelectMany(c => c.Items)
            .Prepend(evaluate.Expression).OfType<DaxNode>(),
        _ => []
    };

    /// <summary>VAR names in scope and the columns of the tables they hold.</summary>
    private sealed class Variables
    {
        public static readonly Variables Empty = new(null, "", DaxQueryColumns.Empty);

        private readonly Variables? _parent;
        private readonly string _name;
        private readonly IReadOnlySet<string> _columns;

        private Variables(Variables? parent, string name, IReadOnlySet<string> columns)
            => (_parent, _name, _columns) = (parent, name, columns);

        public Variables With(string name, IReadOnlySet<string> columns) => new(this, name, columns);

        public IReadOnlySet<string> Lookup(string name)
        {
            for (var scope = this; scope?._parent is not null; scope = scope._parent)
                if (scope._name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return scope._columns;
            return DaxQueryColumns.Empty;
        }
    }

    private sealed class Walker(HashSet<string> defined, Dictionary<int, DaxQueryColumnScope> result)
    {
        public void Visit(DaxNode node, IReadOnlySet<string> scope, Variables variables)
        {
            switch (node)
            {
                case DaxReference { Tokens: [{ Kind: DaxTokenKind.ColumnReference } token] }:
                    var name = Unbracket(token.Text);
                    if (defined.Contains(name))
                        result[token.Start] = scope.Contains(name) ? DaxQueryColumnScope.InScope : DaxQueryColumnScope.OutOfScope;
                    return;

                case DaxCall call when Iterators.TryGetValue(call.FunctionName, out var tableIndex)
                                       && tableIndex < call.Arguments.Count:
                    var rows = Union(scope, Columns(call.Arguments[tableIndex], variables));
                    for (var i = 0; i < call.Arguments.Count; i++)
                        Visit(call.Arguments[i], i > tableIndex ? rows : scope, variables);
                    return;

                case DaxVarReturn block:
                    var scoped = variables;
                    foreach (var variable in block.Variables)
                    {
                        Visit(variable.Value, scope, scoped);
                        scoped = scoped.With(variable.Name.Text, Columns(variable.Value, scoped));
                    }
                    if (block.Body is not null)
                        Visit(block.Body, scope, scoped);
                    return;
            }

            foreach (var child in Children(node))
                Visit(child, scope, variables);
        }
    }
}
