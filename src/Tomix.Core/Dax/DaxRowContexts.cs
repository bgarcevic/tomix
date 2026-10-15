using Tomix.Core.Dax.Engine;

namespace Tomix.Core.Dax;

/// <summary>A model table, or one column of it, whose values a row context carries.</summary>
/// <param name="Table">The table name as written, without quotes.</param>
/// <param name="Column">The one column the row carries; null when it carries every column of the table.</param>
public sealed record DaxRowSource(string Table, string? Column);

/// <summary>One row context around a reference: the rows an iterator walks.</summary>
/// <param name="Sources">The model tables (or single columns) the row carries.</param>
/// <param name="Built">The query-scoped column names the row carries (<c>ADDCOLUMNS</c>, <c>ROW</c>, ...).</param>
/// <param name="Unknown">
/// What the row carries is unknown: it may hold any table's columns. Otherwise it carries
/// exactly <paramref name="Sources"/> and <paramref name="Built"/>.
/// </param>
public sealed record DaxRowContext(
    IReadOnlyList<DaxRowSource> Sources,
    IReadOnlySet<string> Built,
    bool Unknown = false)
{
    internal static readonly DaxRowContext Anything = new([], new HashSet<string>(), Unknown: true);
}

/// <summary>
/// The row contexts around each lone <c>[Name]</c> in an expression, innermost first. DAX binds a
/// lone column used as a value to the innermost row context whose table has it (measured live:
/// <c>SUMX('Fact A', [Revenue])</c> reads <c>'Fact A'[Revenue]</c> even in a measure of another
/// table that has its own <c>[Revenue]</c>), and a lone column passed where a column is expected
/// (<c>SUM([Revenue])</c>) to the expression's own table.
/// </summary>
/// <remarks>
/// Deliberately conservative where it cannot be sure: a table expression it has no rule for is
/// <see cref="DaxRowContext.Unknown"/>, and <c>EARLIER</c>/<c>EARLIEST</c> (which skip row
/// contexts) see an unknown one.
/// </remarks>
public static class DaxRowContexts
{
    /// <summary>
    /// The row contexts around each lone <c>[Name]</c> in <paramref name="dax"/>, innermost first,
    /// keyed by the offset of its opening bracket. A reference outside every iterator maps to an
    /// empty list.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<DaxRowContext>> Analyze(string dax)
    {
        var result = new Dictionary<int, IReadOnlyList<DaxRowContext>>();
        new Walker(result).Visit(DaxParser.Parse(dax), [], Variables.Empty);
        return result;
    }

    /// <summary>Iterators and the index of the table they iterate; every later argument sees its rows.</summary>
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

    /// <summary>Window functions and the index of their relation argument.</summary>
    private static readonly Dictionary<string, int> WindowRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OFFSET"] = 1,
        ["INDEX"] = 1,
        ["WINDOW"] = 4,
        ["RANK"] = 1,
        ["ROWNUMBER"] = 0,
    };

    private static bool IsWindowClause(DaxNode node)
        => node is DaxCall { FunctionName: var name }
           && (name.Equals("ORDERBY", StringComparison.OrdinalIgnoreCase)
               || name.Equals("PARTITIONBY", StringComparison.OrdinalIgnoreCase)
               || name.Equals("MATCHBY", StringComparison.OrdinalIgnoreCase));

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>What a row of the table <paramref name="node"/> evaluates to carries.</summary>
    private static DaxRowContext Rows(DaxNode node, Variables variables)
    {
        switch (node)
        {
            // Sales — a model table, or a VAR holding a table.
            case DaxLeaf { Token.Kind: DaxTokenKind.Identifier } leaf:
                return variables.Lookup(leaf.Token.Text)
                    ?? new DaxRowContext([new DaxRowSource(leaf.Token.Text, null)], None);

            // 'Sales', or 'Sales'[Date] / Sales[Date] where a column stands for its distinct values.
            case DaxReference { Tokens: [{ Kind: DaxTokenKind.QuotedTable or DaxTokenKind.Identifier } table] }:
                return new DaxRowContext([new DaxRowSource(TableName(table), null)], None);
            case DaxReference { Tokens: [{ Kind: DaxTokenKind.QuotedTable or DaxTokenKind.Identifier } table, { Kind: DaxTokenKind.ColumnReference } column] }:
                return new DaxRowContext([new DaxRowSource(TableName(table), Unbracket(column.Text))], None);

            case DaxBracketed { IsBrace: true } braces:
                return new DaxRowContext([], ConstructorColumns(braces));

            case DaxBracketed { Items: [var single] }:
                return Rows(single, variables);

            case DaxVarReturn block:
                var scoped = variables;
                foreach (var variable in block.Variables)
                    scoped = scoped.With(variable.Name.Text, Rows(variable.Value, scoped));
                return block.Body is null ? DaxRowContext.Anything : Rows(block.Body, scoped);

            case DaxCall call:
                return CallRows(call, variables);

            default:
                return DaxRowContext.Anything;
        }
    }

    private static DaxRowContext CallRows(DaxCall call, Variables variables)
    {
        var args = call.Arguments;
        DaxRowContext Arg(int index) => index < args.Count ? Rows(args[index], variables) : DaxRowContext.Anything;

        switch (call.FunctionName.ToUpperInvariant())
        {
            // Same rows (or a subset) with the same columns.
            case "FILTER" or "CALCULATETABLE" or "KEEPFILTERS" or "RELATEDTABLE" or "ALLEXCEPT"
                or "ALLCROSSFILTERED" or "UNION" or "INTERSECT" or "EXCEPT":
                return Arg(0);
            case "TOPN" or "SAMPLE":
                return Arg(1);

            // A table argument keeps its columns; column arguments keep only those columns.
            case "VALUES" or "DISTINCT" or "ALL" or "ALLNOBLANKROW" or "ALLSELECTED" or "REMOVEFILTERS":
                if (args.Count == 1)
                    return Arg(0);
                return args.All(a => QualifiedColumn(a) is not null)
                    ? new DaxRowContext([.. args.Select(a => QualifiedColumn(a)!)], None)
                    : DaxRowContext.Anything;

            case "ADDCOLUMNS":
                var added = Arg(0);
                return added with { Built = Union(added.Built, NewColumns(call)) };

            // Only what it lists: the new names (a bare column it lists keeps its lineage).
            case "SELECTCOLUMNS":
                if (args.Count % 2 == 0)
                    return DaxRowContext.Anything;
                return new DaxRowContext([], NewColumns(call));

            // The group-by columns plus the new names.
            case "SUMMARIZE" or "GROUPBY" or "SUMMARIZECOLUMNS":
                var groupBy = new List<DaxRowSource>();
                foreach (var argument in args.Skip(call.FunctionName.Equals("SUMMARIZECOLUMNS", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    if (Literal(argument) is not null)
                        break;
                    if (QualifiedColumn(argument) is { } column)
                        groupBy.Add(column);
                    // SUMMARIZECOLUMNS also takes filter tables, which add no columns.
                    else if (!call.FunctionName.Equals("SUMMARIZECOLUMNS", StringComparison.OrdinalIgnoreCase))
                        return DaxRowContext.Anything;
                }
                return new DaxRowContext(groupBy, NewColumns(call));

            // Every argument's columns side by side.
            case "CROSSJOIN" or "GENERATE" or "GENERATEALL" or "NATURALINNERJOIN" or "NATURALLEFTOUTERJOIN":
                var parts = args.Select(a => Rows(a, variables)).ToList();
                return parts.Count == 0 || parts.Any(p => p.Unknown)
                    ? DaxRowContext.Anything
                    : new DaxRowContext(
                        [.. parts.SelectMany(p => p.Sources)],
                        parts.Aggregate(None, (acc, p) => Union(acc, p.Built)));

            case "ROW" or "DATATABLE":
                return new DaxRowContext([], NewColumns(call));
            case "GENERATESERIES":
                return new DaxRowContext([], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Value" });

            default:
                return DaxRowContext.Anything;
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

    private static DaxRowSource? QualifiedColumn(DaxNode node)
        => node is DaxReference { Tokens: [{ Kind: DaxTokenKind.QuotedTable or DaxTokenKind.Identifier } table, { Kind: DaxTokenKind.ColumnReference } column] }
            ? new DaxRowSource(TableName(table), Unbracket(column.Text))
            : null;

    private static void AddLiteral(DaxNode node, HashSet<string> names)
    {
        if (Literal(node) is { } name)
            names.Add(name);
    }

    private static string? Literal(DaxNode node)
        => node is DaxLeaf { Token: { Kind: DaxTokenKind.String, Text: var text } } && text.Length >= 2
            ? text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : null;

    private static string TableName(DaxToken token)
        => token.Kind == DaxTokenKind.QuotedTable && token.Text.Length >= 2
            ? token.Text[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : token.Text;

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
        DaxCall call => call.Arguments.Prepend(call.Callee),
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

    /// <summary>VAR names in scope and the rows of the tables they hold.</summary>
    private sealed class Variables
    {
        public static readonly Variables Empty = new(null, "", DaxRowContext.Anything);

        private readonly Variables? _parent;
        private readonly string _name;
        private readonly DaxRowContext _rows;

        private Variables(Variables? parent, string name, DaxRowContext rows)
            => (_parent, _name, _rows) = (parent, name, rows);

        public Variables With(string name, DaxRowContext rows) => new(this, name, rows);

        public DaxRowContext? Lookup(string name)
        {
            for (var scope = this; scope?._parent is not null; scope = scope._parent)
                if (scope._name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return scope._rows;
            return null;
        }
    }

    private sealed class Walker(Dictionary<int, IReadOnlyList<DaxRowContext>> result)
    {
        public void Visit(DaxNode node, IReadOnlyList<DaxRowContext> stack, Variables variables)
        {
            switch (node)
            {
                case DaxReference { Tokens: [{ Kind: DaxTokenKind.ColumnReference } token] }:
                    result[token.Start] = stack;
                    return;

                // The parser reads a filter such as [Region] = "EU" as a "[Name] = expr"
                // definition; in a model expression it is a comparison.
                case DaxDefinition { StartsWithKeyword: false, NameTokens: [{ Kind: DaxTokenKind.ColumnReference } name] } definition:
                    result[name.Start] = stack;
                    if (definition.Value is not null)
                        Visit(definition.Value, stack, variables);
                    return;

                case DaxCall call when Iterators.TryGetValue(call.FunctionName, out var tableIndex)
                                       && tableIndex < call.Arguments.Count:
                    IReadOnlyList<DaxRowContext> inner = [Rows(call.Arguments[tableIndex], variables), .. stack];
                    for (var i = 0; i < call.Arguments.Count; i++)
                        Visit(call.Arguments[i], i > tableIndex ? inner : stack, variables);
                    return;

                // OFFSET(-1, ALL(T[Year]), ORDERBY([Year])): the ORDERBY, PARTITIONBY and MATCHBY
                // columns are columns of the relation (measured live). Without a relation the
                // columns come from the filter context, which is not tracked.
                case DaxCall call when WindowRelations.TryGetValue(call.FunctionName, out var relationIndex):
                    var relation = relationIndex < call.Arguments.Count && !IsWindowClause(call.Arguments[relationIndex])
                        ? Rows(call.Arguments[relationIndex], variables)
                        : DaxRowContext.Anything;
                    IReadOnlyList<DaxRowContext> rows = [relation, .. stack];
                    foreach (var argument in call.Arguments)
                    {
                        if (IsWindowClause(argument))
                            foreach (var column in ((DaxCall)argument).Arguments)
                                Visit(column, rows, variables);
                        else
                            Visit(argument, stack, variables);
                    }
                    return;

                // EARLIER([X]) reads an outer row context; which one is not tracked.
                case DaxCall call when call.FunctionName.Equals("EARLIER", StringComparison.OrdinalIgnoreCase)
                                       || call.FunctionName.Equals("EARLIEST", StringComparison.OrdinalIgnoreCase):
                    IReadOnlyList<DaxRowContext> unknown = [DaxRowContext.Anything, .. stack];
                    foreach (var argument in call.Arguments)
                        Visit(argument, unknown, variables);
                    return;

                case DaxVarReturn block:
                    var scoped = variables;
                    foreach (var variable in block.Variables)
                    {
                        Visit(variable.Value, stack, scoped);
                        scoped = scoped.With(variable.Name.Text, Rows(variable.Value, scoped));
                    }
                    if (block.Body is not null)
                        Visit(block.Body, stack, scoped);
                    return;
            }

            foreach (var child in Children(node))
                Visit(child, stack, variables);
        }
    }
}
