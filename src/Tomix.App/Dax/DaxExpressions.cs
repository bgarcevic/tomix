using Tomix.Core.Models;

namespace Tomix.App.Dax;

/// <summary>A DAX-bearing property of a model object: the snapshot property key and its text.
/// Key "Expression" is the object's main expression; other keys match the snapshot contract
/// ("DetailRowsExpression", "KpiTargetExpression", ...).</summary>
public readonly record struct DaxSite(string Property, string Expression);

/// <summary>
/// Yields every DAX-bearing string attached to a model object so dependency analysis can scan
/// beyond a measure's main <see cref="ModelObject.Expression"/>. This mirrors Tabular Editor's set
/// of <c>DAXProperty</c> values: detail-rows, format-string and KPI expressions on measures,
/// default detail-rows expressions on tables, calculated column / calculation item expressions,
/// calculated-table DAX (carried on the partition), and role RLS filters.
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

    /// <summary>Enumerates the DAX expressions to scan for <paramref name="obj"/> (may be empty).</summary>
    public static IEnumerable<string> ForObject(ModelObject obj)
        => Sites(obj).Select(site => site.Expression);

    /// <summary>
    /// Enumerates the DAX expressions of <paramref name="obj"/> with the property key each lives
    /// under, so a rewrite can be routed back to the right property.
    /// </summary>
    public static IEnumerable<DaxSite> Sites(ModelObject obj)
    {
        switch (obj.Kind)
        {
            // A table's default detail-rows (drillthrough) DAX.
            case ModelObjectKind.Table:
                var detailRows = obj.Property("DefaultDetailRowsExpression");
                if (!string.IsNullOrWhiteSpace(detailRows))
                    yield return new DaxSite("DefaultDetailRowsExpression", detailRows!);
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

            // Calculated columns and calculation items carry their DAX directly on Expression.
            case ModelObjectKind.Column:
            case ModelObjectKind.CalculationItem:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite("Expression", obj.Expression!);
                break;

            // A calculated table's DAX lives on its (Calculated-source) partition; M-query
            // partitions must not be parsed as DAX.
            case ModelObjectKind.Partition:
                if (IsCalculated(obj) && !string.IsNullOrWhiteSpace(obj.Expression))
                    yield return new DaxSite("Expression", obj.Expression!);
                break;

            // Role row-level-security filters (one filter per line: "Table: <DAX>").
            case ModelObjectKind.Role:
                var rls = obj.Property("RlsExpression");
                if (!string.IsNullOrWhiteSpace(rls))
                    yield return new DaxSite("RlsExpression", rls!);
                break;
        }
    }

    private static bool IsCalculated(ModelObject partition)
        => string.Equals(partition.Property("PartitionSourceType"), "Calculated", StringComparison.OrdinalIgnoreCase)
           || string.Equals(partition.Detail, "calculated", StringComparison.OrdinalIgnoreCase);
}
