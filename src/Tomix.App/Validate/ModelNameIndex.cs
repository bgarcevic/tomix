using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.Core.Dax;
using Tomix.Core.Models;

namespace Tomix.App.Validate;

/// <summary>
/// Name lookups shared by the DAX and structural checks of <see cref="ModelValidation"/> and
/// by the <c>query</c> pre-flight (<see cref="Query.QueryPreflight"/>). <paramref name="UnknownColumnTables"/>
/// holds calculated tables whose columns are neither declared nor inferable offline, so a
/// column reference into them cannot be judged.
/// </summary>
internal sealed record ModelNameIndex(
    Dictionary<string, HashSet<string>> TableColumns,
    HashSet<string> MeasureNames,
    HashSet<string> ColumnNames,
    HashSet<string> CalendarNames,
    HashSet<string> UnknownColumnTables)
{
    public static ModelNameIndex Build(IReadOnlyList<ModelObject> objects)
    {
        var tableColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var measureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownColumnTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var calendarNames = objects
            .Where(o => o.Kind == ModelObjectKind.Calendar)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var table in objects.Where(o => o.Kind == ModelObjectKind.Table))
        {
            var columns = table.Children
                .Where(c => c.Kind is ModelObjectKind.Column or ModelObjectKind.CalculatedColumn)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A calculated table saved before it was ever evaluated (hand-written or generated
            // TMDL) declares no columns; the engine derives them from the DAX on refresh.
            var calculated = table.Children.FirstOrDefault(c =>
                c.Kind == ModelObjectKind.Partition && DaxExpressions.IsCalculated(c));
            if (columns.Count == 0 && calculated is not null)
            {
                if (DaxQueryColumns.CalculatedTableColumns(calculated.Expression) is { } inferred)
                    columns.UnionWith(inferred);
                else
                    unknownColumnTables.Add(table.Name);
            }

            tableColumns.TryAdd(table.Name, columns);
            columnNames.UnionWith(columns);

            foreach (var measure in table.Children.Where(c => c.Kind == ModelObjectKind.Measure))
                measureNames.Add(measure.Name);
        }

        return new ModelNameIndex(tableColumns, measureNames, columnNames, calendarNames, unknownColumnTables);
    }
}
