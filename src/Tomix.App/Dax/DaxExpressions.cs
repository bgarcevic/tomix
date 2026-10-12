using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Dax;

/// <summary>A DAX-bearing property of a model object: the snapshot property key and its text.
/// Key "Expression" is the object's main expression; other keys match the snapshot contract
/// ("DetailRowsExpression", "KpiTargetExpression", ...).</summary>
public readonly record struct DaxSite(string Property, string Expression);

/// <summary>
/// Yields every DAX-bearing string attached to a model object so dependency analysis can scan
/// beyond a measure's main <see cref="ModelObject.Expression"/>. This mirrors Tabular Editor's set
/// of <c>DAXProperty</c> values: detail-rows, format-string and KPI expressions on measures,
/// default detail-rows and calculation group selection expressions on tables, calculated column
/// and calculation item expressions (plus an item's format string), calculated-table DAX
/// (carried on the partition), and each table permission's RLS filter.
/// </summary>
public static class DaxExpressions
{
    // Property keys are part of the provider-agnostic snapshot contract (see TomModelSummarizer).
    private static readonly string[] MeasureExpressionProperties =
    [
        "DetailRowsExpression",
        "FormatStringExpression",
        "KpiTargetExpression",
        "KpiStatusExpression",
        "KpiTrendExpression",
    ];

    private static readonly string[] TableExpressionProperties =
    [
        PropertyBagKeys.DefaultDetailRowsExpression,
        PropertyBagKeys.NoSelectionExpression,
        PropertyBagKeys.MultipleOrEmptySelectionExpression,
    ];

    /// <summary>The site key of a table permission's RLS filter, which the snapshot carries on
    /// its <see cref="ModelObject.Expression"/>; named after the property <c>set</c> takes.</summary>
    public const string FilterExpressionProperty = "FilterExpression";

    /// <summary>Enumerates the DAX expressions to scan for <paramref name="obj"/> (may be empty).</summary>
    public static IEnumerable<string> ForObject(ModelObject obj)
        => Sites(obj).Select(site => site.Expression);

    /// <summary>
    /// Whether <paramref name="text"/> is one of <paramref name="obj"/>'s DAX expressions —
    /// the check output renderers use to decide between highlighting and plain text.
    /// </summary>
    public static bool IsDaxValue(ModelObject obj, string text)
        => ForObject(obj).Contains(text, StringComparer.Ordinal);

    /// <summary>
    /// Whether an object's main <c>Expression</c> text is DAX rather than M or none, for
    /// renderers that hold only the flattened kind/detail projection (no property bag).
    /// Mirrors <see cref="Sites"/>: measures, calculated columns, calculation items, functions
    /// and table permissions (RLS filters) carry DAX, as do calculated-table partitions (detail
    /// "calculated"); M-query partitions and shared expressions are M.
    /// </summary>
    public static bool IsDaxExpression(ModelObjectKind kind, string? detail)
        => kind is ModelObjectKind.Measure
            or ModelObjectKind.Column
            or ModelObjectKind.CalculatedColumn
            or ModelObjectKind.CalculationItem
            or ModelObjectKind.Function
            or ModelObjectKind.TablePermission
            || (kind is ModelObjectKind.Partition
                && string.Equals(detail, "calculated", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a property key can name a DAX value on some object kind — the union of the keys
    /// <see cref="Sites"/> yields. A candidate edit uses this to decide whether resolving the
    /// object's current value is worth a snapshot read; the authoritative check is
    /// <see cref="Sites"/> against the actual object.
    /// </summary>
    public static bool MayBeDaxProperty(string property)
    {
        var key = property.Trim();
        return string.Equals(key, "Expression", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, FilterExpressionProperty, StringComparison.OrdinalIgnoreCase)
            || TableExpressionProperties.Contains(key, StringComparer.OrdinalIgnoreCase)
            || MeasureExpressionProperties.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates the DAX expressions of <paramref name="obj"/> with the property key each lives
    /// under, so a rewrite can be routed back to the right property.
    /// </summary>
    public static IEnumerable<DaxSite> Sites(ModelObject obj)
    {
        switch (obj.Kind)
        {
            // A table's default detail-rows (drillthrough) DAX, and a calculation group's
            // no-selection and multiple-or-empty-selection expressions.
            case ModelObjectKind.Table:
                foreach (var key in TableExpressionProperties)
                {
                    var value = obj.Property(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return new DaxSite(key, value!);
                }
                break;

            case ModelObjectKind.Measure:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite("Expression", obj.Expression!);
                foreach (var key in MeasureExpressionProperties)
                {
                    var value = obj.Property(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return new DaxSite(key, value!);
                }
                break;

            // Calculated columns, calculation items, and user-defined function bodies carry
            // their DAX directly on Expression. Shared expressions (ModelObjectKind.Expression)
            // are M and must never be scanned as DAX.
            case ModelObjectKind.Column:
            case ModelObjectKind.CalculatedColumn:
            case ModelObjectKind.CalculationItem:
            case ModelObjectKind.Function:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite("Expression", obj.Expression!);
                // A calculation item's dynamic format string.
                if (obj.Kind == ModelObjectKind.CalculationItem
                    && obj.Property(PropertyBagKeys.FormatStringExpression) is { } itemFormat
                    && !string.IsNullOrWhiteSpace(itemFormat))
                    yield return new DaxSite(PropertyBagKeys.FormatStringExpression, itemFormat);
                break;

            // A calculated table's DAX lives on its (Calculated-source) partition; M-query
            // partitions must not be parsed as DAX.
            case ModelObjectKind.Partition:
                if (IsCalculated(obj) && !string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite("Expression", obj.Expression!);
                break;

            // A row-level-security filter is checked on its own table permission. The role's
            // RlsExpression only lists them for display ("Table: <DAX>" per line) and is not DAX.
            case ModelObjectKind.TablePermission:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite(FilterExpressionProperty, obj.Expression!);
                break;
        }
    }

    internal static bool IsCalculated(ModelObject partition)
        => string.Equals(partition.Property("PartitionSourceType"), "Calculated", StringComparison.OrdinalIgnoreCase)
           || string.Equals(partition.Detail, "calculated", StringComparison.OrdinalIgnoreCase);
}
