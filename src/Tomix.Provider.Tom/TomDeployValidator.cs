using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Provider.Tom;

/// <summary>
/// The post-deploy pass (#237): walks the deployed database as the server reports it and
/// lists every object whose state is not <see cref="ObjectState.Ready"/>. The server accepts
/// TMSL whose DAX does not resolve, so this is where a broken measure or RLS filter surfaces.
/// </summary>
public static class TomDeployValidator
{
    public static DeployValidation Inspect(Database database)
    {
        var issues = new List<DeployedObjectIssue>();
        var model = database.Model;

        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Type == ColumnType.RowNumber)
                    continue;

                var type = column.Type == ColumnType.Data ? "Column" : column.GetType().Name;
                Add(issues, type, Qualified(table.Name, column.Name), column.State, column.ErrorMessage);
            }

            foreach (var measure in table.Measures)
            {
                var name = Qualified(table.Name, measure.Name);
                Add(issues, "Measure", name, measure.State, measure.ErrorMessage);
                if (measure.FormatStringDefinition is { } format)
                    Add(issues, "FormatStringDefinition", name, format.State, format.ErrorMessage);
                if (measure.DetailRowsDefinition is { } detailRows)
                    Add(issues, "DetailRowsDefinition", name, detailRows.State, detailRows.ErrorMessage);
            }

            foreach (var partition in table.Partitions)
                Add(issues, "Partition", Qualified(table.Name, partition.Name), partition.State, partition.ErrorMessage);

            foreach (var hierarchy in table.Hierarchies)
                Add(issues, "Hierarchy", Qualified(table.Name, hierarchy.Name), hierarchy.State, message: null);

            if (table.CalculationGroup is { } group)
            {
                foreach (var item in group.CalculationItems)
                {
                    var name = Qualified(table.Name, item.Name);
                    Add(issues, "CalculationItem", name, item.State, item.ErrorMessage);
                    if (item.FormatStringDefinition is { } format)
                        Add(issues, "FormatStringDefinition", name, format.State, format.ErrorMessage);
                }

                if (group.MultipleOrEmptySelectionExpression is { } multiple)
                    Add(issues, "CalculationGroupExpression", Qualified(table.Name, "MultipleOrEmptySelection"),
                        multiple.State, multiple.ErrorMessage);
                if (group.NoSelectionExpression is { } none)
                    Add(issues, "CalculationGroupExpression", Qualified(table.Name, "NoSelection"),
                        none.State, none.ErrorMessage);
            }
        }

        foreach (var relationship in model.Relationships)
            Add(issues, "Relationship", relationship.Name, relationship.State, message: null);

        foreach (var role in model.Roles)
        {
            foreach (var permission in role.TablePermissions)
                Add(issues, "TablePermission", Qualified(role.Name, permission.Name), permission.State, permission.ErrorMessage);
        }

        foreach (var function in model.Functions)
            Add(issues, "Function", function.Name, function.State, function.ErrorMessage);

        // Errors first so a long unprocessed list never buries the objects that are broken;
        // OrderBy is stable, so model order holds within a severity.
        return new DeployValidation([.. issues.OrderByDescending(issue => issue.Severity)]);
    }

    private static void Add(
        List<DeployedObjectIssue> issues, string objectType, string objectName, ObjectState state, string? message)
    {
        if (Severity(state) is not { } severity)
            return;

        issues.Add(new DeployedObjectIssue(
            severity, objectType, objectName, state.ToString(),
            string.IsNullOrWhiteSpace(message) ? null : message));
    }

    /// <summary>
    /// Errors are objects the server cannot evaluate; an incomplete object is defined but not
    /// usable yet; no-data and calculation-needed objects only await a refresh, which a
    /// metadata deploy never runs. Null means ready.
    /// </summary>
    internal static RuleSeverity? Severity(ObjectState state) => state switch
    {
        ObjectState.SemanticError or ObjectState.EvaluationError or ObjectState.DependencyError => RuleSeverity.Error,
        ObjectState.Incomplete => RuleSeverity.Warning,
        ObjectState.NoData or ObjectState.CalculationNeeded or ObjectState.ForceCalculationNeeded => RuleSeverity.Info,
        _ => null
    };

    private static string Qualified(string parent, string name) => $"{parent}[{name}]";
}
