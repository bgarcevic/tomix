using System.Text;
using Tomix.App.Dax;
using Tomix.App.Mutations;
using Tomix.Core.Models;

namespace Tomix.App.Bpa;

/// <summary>The DAX-rewrite fix intrinsics a rule's <c>FixExpression</c> can name.</summary>
internal enum BpaDaxRewrite
{
    /// <summary><c>QualifyColumnReferences()</c>: <c>[Col]</c> → <c>'Table'[Col]</c>.</summary>
    QualifyColumnReferences,

    /// <summary><c>UnqualifyMeasureReferences()</c>: <c>'Table'[Measure]</c> → <c>[Measure]</c>.</summary>
    UnqualifyMeasureReferences,
}

/// <summary>
/// Rewrites column/measure reference qualification in one DAX expression, resolving each
/// reference exactly as <see cref="Model.BpaModelBuilder"/> does for <c>DependsOn</c> (a lone
/// <c>[X]</c> is a measure first, a qualified one a column first), so a rewrite resolves exactly
/// the references the qualification rules flag. References are spliced by span, so formatting
/// and comments survive. Anything the rewrite cannot resolve with certainty fails the whole
/// expression instead of being guessed.
/// </summary>
internal static class BpaDaxRewriter
{
    public static bool TryParse(string fixExpression, out BpaDaxRewrite rewrite)
    {
        var name = fixExpression.Trim();
        if (name.EndsWith("()", StringComparison.Ordinal)
            && Enum.TryParse(name[..^2], ignoreCase: true, out rewrite)
            && Enum.IsDefined(rewrite))
            return true;

        rewrite = default;
        return false;
    }

    /// <summary>
    /// The rewritten expression, unchanged text when nothing needed rewriting, or an error naming
    /// every reference that could not be rewritten safely (the expression is then left alone).
    /// </summary>
    public static (string? Text, string? Error) Rewrite(string expression, BpaDaxRewrite rewrite, BpaDaxNames names)
    {
        // A string literal can name a query-scoped column (ADDCOLUMNS(..., "Amount", ...)) that a
        // bracketed [Amount] then resolves to; re-qualifying such a reference would change what it
        // means, so any name that also appears as a literal is left for a human to decide.
        var literals = new HashSet<string>(DaxTokenizer.StringLiterals(expression), StringComparer.OrdinalIgnoreCase);
        var edits = new List<(int Start, int End, string Text)>();
        var errors = new List<string>();

        foreach (var reference in DaxReferenceExtractor.Extract(expression))
        {
            if (reference.Object is not { } name)
                continue;

            if (rewrite == BpaDaxRewrite.QualifyColumnReferences)
            {
                if (reference.Shape != DaxReferenceShape.Unqualified
                    || names.IsMeasure(name)
                    || names.ColumnTables(name) is not { Count: > 0 } tables)
                    continue;

                if (tables.Count > 1)
                    errors.Add($"column [{name}] exists in tables {string.Join(", ", tables.Select(RenameFixup.QuoteTable))}");
                else if (literals.Contains(name))
                    errors.Add($"[{name}] may name a query-scoped column (\"{name}\" is also a string literal)");
                else
                    edits.Add((reference.Start, reference.End,
                        RenameFixup.QuoteTable(tables[0]) + expression[reference.Start..(reference.End + 1)]));
            }
            else
            {
                if (reference.Shape != DaxReferenceShape.Qualified
                    || names.ColumnTables(name) is { Count: > 0 }
                    || names.MeasureTable(name) is not { } home)
                    continue;

                if (!string.Equals(reference.Table, home, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"measure [{name}] is qualified with {RenameFixup.QuoteTable(reference.Table!)} but lives in {RenameFixup.QuoteTable(home)}");
                else if (literals.Contains(name))
                    errors.Add($"[{name}] may name a query-scoped column (\"{name}\" is also a string literal)");
                else
                    edits.Add((reference.Start, reference.End, RenameFixup.Bracket(name)));
            }
        }

        if (errors.Count > 0)
            return (null, "cannot rewrite safely: " + string.Join("; ", errors.Distinct(StringComparer.OrdinalIgnoreCase)));

        // Splice from last to first so earlier offsets stay valid.
        var text = new StringBuilder(expression);
        foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start))
        {
            text.Remove(start, end - start + 1);
            text.Insert(start, replacement);
        }

        return (text.ToString(), null);
    }
}

/// <summary>
/// The model's measure and column names with their tables — the name resolution the rewrite
/// shares with <see cref="Model.BpaModelBuilder"/>'s <c>DependsOn</c>.
/// </summary>
internal sealed class BpaDaxNames
{
    private readonly Dictionary<string, string> _measureTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _columnTables = new(StringComparer.OrdinalIgnoreCase);

    public static BpaDaxNames FromSnapshot(ModelSnapshot snapshot)
    {
        var names = new BpaDaxNames();
        foreach (var table in snapshot.Objects.Where(o => o.Kind == ModelObjectKind.Table))
        {
            foreach (var child in table.Children)
            {
                switch (child.Kind)
                {
                    case ModelObjectKind.Measure:
                        names._measureTables.TryAdd(child.Name, table.Name);
                        break;
                    case ModelObjectKind.Column:
                    case ModelObjectKind.CalculatedColumn:
                        if (!names._columnTables.TryGetValue(child.Name, out var tables))
                            names._columnTables[child.Name] = tables = [];
                        if (!tables.Contains(table.Name, StringComparer.OrdinalIgnoreCase))
                            tables.Add(table.Name);
                        break;
                }
            }
        }

        return names;
    }

    public bool IsMeasure(string name) => _measureTables.ContainsKey(name);

    /// <summary>The table holding measure <paramref name="name"/> (measure names are model-unique).</summary>
    public string? MeasureTable(string name) => _measureTables.GetValueOrDefault(name);

    /// <summary>Every table with a column named <paramref name="name"/>, or null when none does.</summary>
    public IReadOnlyList<string>? ColumnTables(string name) => _columnTables.GetValueOrDefault(name);
}
