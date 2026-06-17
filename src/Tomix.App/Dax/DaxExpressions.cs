using Tomix.Core.Models;

namespace Tomix.App.Dax;

/// <summary>
/// Yields every DAX-bearing string attached to a model object so dependency analysis can scan
/// beyond a measure's main <see cref="ModelObject.Expression"/>. This mirrors Tabular Editor's set
/// of <c>DAXProperty</c> values: detail-rows, format-string and KPI expressions on measures,
/// calculated column / calculation item expressions, calculated-table DAX (carried on the
/// partition), and role RLS filters.
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
    {
        switch (obj.Kind)
        {
            case ModelObjectKind.Measure:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return obj.Expression!;
                foreach (var key in MeasureExpressionProperties)
                {
                    var value = obj.Property(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value!;
                }
                break;

            // Calculated columns and calculation items carry their DAX directly on Expression.
            case ModelObjectKind.Column:
            case ModelObjectKind.CalculationItem:
                if (!string.IsNullOrWhiteSpace(obj.Expression))
                    yield return obj.Expression!;
                break;

            // A calculated table's DAX lives on its (Calculated-source) partition; M-query
            // partitions must not be parsed as DAX.
            case ModelObjectKind.Partition:
                if (IsCalculated(obj) && !string.IsNullOrWhiteSpace(obj.Expression))
                    yield return obj.Expression!;
                break;

            // Role row-level-security filters (one filter per line: "Table: <DAX>").
            case ModelObjectKind.Role:
                var rls = obj.Property("RlsExpression");
                if (!string.IsNullOrWhiteSpace(rls))
                    yield return rls!;
                break;
        }
    }

    private static bool IsCalculated(ModelObject partition)
        => string.Equals(partition.Property("PartitionSourceType"), "Calculated", StringComparison.OrdinalIgnoreCase)
           || string.Equals(partition.Detail, "calculated", StringComparison.OrdinalIgnoreCase);
}
