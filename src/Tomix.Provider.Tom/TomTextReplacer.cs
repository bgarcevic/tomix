using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using static Tomix.Provider.Tom.TomMutationPaths;

namespace Tomix.Provider.Tom;

/// <summary>
/// Model-wide text find/replace across names, expressions, descriptions, display folders,
/// format strings, and (explicit-only) annotations, with per-property previews. The walk must
/// cover every property the catalog marks searchable — find previews what replace rewrites —
/// and CatalogSearchableAgreementTests fails when the two drift apart.
/// </summary>
internal sealed class TomTextReplacer
{
    private readonly Database _database;

    public TomTextReplacer(Database database) => _database = database;

    public ModelReplaceResult Replace(ModelReplaceRequest request)
    {
        var operations = BuildReplaceOperations(request)
            .Where(o => !string.Equals(o.Preview.Before, o.Preview.After, StringComparison.Ordinal))
            .ToList();
        if (request.Apply)
        {
            foreach (var operation in operations)
                operation.Apply(operation.Preview.After);
        }

        return new ModelReplaceResult(
            operations.Count,
            operations.Select(o => o.Preview).ToList());
    }

    /// <summary>
    /// Single model walk; each object yields the property sites belonging to the requested
    /// scope. Under scope "all" the previews therefore group by object, not by property kind.
    /// </summary>
    private IEnumerable<ReplaceOperation> BuildReplaceOperations(ModelReplaceRequest request)
    {
        var scope = NormalizeReplaceScope(request.Scope);

        // Annotations are explicit-only: values are often tool-generated JSON, so a blanket
        // '--in all' replace must not rewrite them.
        bool In(string candidate) => scope == candidate || (scope == "all" && candidate != "annotations");

        ReplaceOperation Op(string path, string property, string? value, Action<string> apply)
            => ReplaceProperty(path, property, value, apply, request);

        IEnumerable<ReplaceOperation> AnnotationOps(string path, IEnumerable<Annotation> annotations)
        {
            foreach (var annotation in annotations)
            {
                var target = annotation;
                yield return Op(path, $"Annotation:{annotation.Name}", target.Value, v => target.Value = v);
            }
        }

        if (In("descriptions"))
            yield return Op(".", "Description", _database.Model.Description, v => _database.Model.Description = v);
        if (In("annotations"))
        {
            foreach (var op in AnnotationOps(".", _database.Model.Annotations))
                yield return op;
        }

        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            if (In("names"))
                yield return Op(tablePath, "Name", table.Name, v => table.Name = v);
            if (In("descriptions"))
                yield return Op(tablePath, "Description", table.Description, v => table.Description = v);
            if (In("expressions"))
            {
                if (table.DefaultDetailRowsDefinition is { } defaultDetailRows)
                    yield return Op(tablePath, "DefaultDetailRowsExpression", defaultDetailRows.Expression, v => defaultDetailRows.Expression = v);
                if (table.RefreshPolicy is BasicRefreshPolicy policy)
                {
                    yield return Op(tablePath, "RefreshPolicySourceExpression", policy.SourceExpression, v => policy.SourceExpression = v);
                    yield return Op(tablePath, "RefreshPolicyPollingExpression", policy.PollingExpression, v => policy.PollingExpression = v);
                }

                if (table.CalculationGroup is { } group)
                {
                    if (group.NoSelectionExpression is { } noSelection)
                        yield return Op(tablePath, "NoSelectionExpression", noSelection.Expression, v => noSelection.Expression = v);
                    if (group.MultipleOrEmptySelectionExpression is { } multiSelection)
                        yield return Op(tablePath, "MultipleOrEmptySelectionExpression", multiSelection.Expression, v => multiSelection.Expression = v);
                }
            }

            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(tablePath, table.Annotations))
                    yield return op;
            }

            foreach (var measure in table.Measures)
            {
                var path = $"{tablePath}/{Segment(measure.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", measure.Name, v => measure.Name = v);
                if (In("expressions"))
                {
                    yield return Op(path, "Expression", measure.Expression, v => measure.Expression = v);
                    if (measure.DetailRowsDefinition is { } detailRows)
                        yield return Op(path, "DetailRowsExpression", detailRows.Expression, v => detailRows.Expression = v);
                    if (measure.FormatStringDefinition is { } formatStringDefinition)
                        yield return Op(path, "FormatStringExpression", formatStringDefinition.Expression, v => formatStringDefinition.Expression = v);
                }

                if (In("descriptions"))
                    yield return Op(path, "Description", measure.Description, v => measure.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", measure.DisplayFolder, v => measure.DisplayFolder = v);
                if (In("formatstrings"))
                    yield return Op(path, "FormatString", measure.FormatString, v => measure.FormatString = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, measure.Annotations))
                        yield return op;
                }

                if (measure.KPI is { } kpi)
                {
                    var kpiPath = $"{path}/KPI";
                    if (In("expressions"))
                    {
                        yield return Op(kpiPath, "TargetExpression", kpi.TargetExpression, v => kpi.TargetExpression = v);
                        yield return Op(kpiPath, "StatusExpression", kpi.StatusExpression, v => kpi.StatusExpression = v);
                        yield return Op(kpiPath, "TrendExpression", kpi.TrendExpression, v => kpi.TrendExpression = v);
                    }

                    if (In("descriptions"))
                        yield return Op(kpiPath, "Description", kpi.Description, v => kpi.Description = v);
                    if (In("formatstrings"))
                        yield return Op(kpiPath, "TargetFormatString", kpi.TargetFormatString, v => kpi.TargetFormatString = v);
                    if (In("annotations"))
                    {
                        foreach (var op in AnnotationOps(kpiPath, kpi.Annotations))
                            yield return op;
                    }
                }
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var path = $"{tablePath}/{Segment(column.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", column.Name, v => column.Name = v);
                if (In("expressions") && column is CalculatedColumn calculated)
                    yield return Op(path, "Expression", calculated.Expression, v => calculated.Expression = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", column.Description, v => column.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", column.DisplayFolder, v => column.DisplayFolder = v);
                if (In("formatstrings"))
                    yield return Op(path, "FormatString", column.FormatString, v => column.FormatString = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, column.Annotations))
                        yield return op;
                }
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var path = $"{tablePath}/{Segment(hierarchy.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", hierarchy.Name, v => hierarchy.Name = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", hierarchy.Description, v => hierarchy.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", hierarchy.DisplayFolder, v => hierarchy.DisplayFolder = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, hierarchy.Annotations))
                        yield return op;
                }

                foreach (var level in hierarchy.Levels)
                {
                    var levelPath = $"{path}/{Segment(level.Name)}";
                    if (In("names"))
                        yield return Op(levelPath, "Name", level.Name, v => level.Name = v);
                    if (In("descriptions"))
                        yield return Op(levelPath, "Description", level.Description, v => level.Description = v);
                    if (In("annotations"))
                    {
                        foreach (var op in AnnotationOps(levelPath, level.Annotations))
                            yield return op;
                    }
                }
            }

            if (table.CalculationGroup is { } calculationGroup)
            {
                foreach (var item in calculationGroup.CalculationItems)
                {
                    var path = $"{tablePath}/{Segment(item.Name)}";
                    if (In("names"))
                        yield return Op(path, "Name", item.Name, v => item.Name = v);
                    if (In("expressions"))
                    {
                        yield return Op(path, "Expression", item.Expression, v => item.Expression = v);
                        if (item.FormatStringDefinition is { } formatStringDefinition)
                            yield return Op(path, "FormatStringExpression", formatStringDefinition.Expression, v => formatStringDefinition.Expression = v);
                    }

                    if (In("descriptions"))
                        yield return Op(path, "Description", item.Description, v => item.Description = v);
                }
            }

            foreach (var calendar in table.Calendars)
            {
                var path = $"{tablePath}/{Segment(calendar.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", calendar.Name, v => calendar.Name = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", calendar.Description, v => calendar.Description = v);
            }

            foreach (var partition in table.Partitions)
            {
                var path = $"{tablePath}/{Segment(partition.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", partition.Name, v => partition.Name = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", partition.Description, v => partition.Description = v);
                if (In("expressions"))
                {
                    switch (partition.Source)
                    {
                        case MPartitionSource m:
                            yield return Op(path, "Expression", m.Expression, v => m.Expression = v);
                            break;
                        case CalculatedPartitionSource calculated:
                            yield return Op(path, "Expression", calculated.Expression, v => calculated.Expression = v);
                            break;
                    }
                }

                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, partition.Annotations))
                        yield return op;
                }
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var path = $"Roles/{Segment(role.Name)}";
            if (In("names"))
                yield return Op(path, "Name", role.Name, v => role.Name = v);
            if (In("descriptions"))
                yield return Op(path, "Description", role.Description, v => role.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, role.Annotations))
                    yield return op;
            }

            foreach (var member in role.Members)
            {
                var memberPath = $"{path}/{Segment(member.MemberName)}";
                if (In("names"))
                {
                    // TOM freezes MemberName, so the rename replaces the member (safe here:
                    // annotation ops only exist under the explicit annotations scope, never
                    // alongside name ops, so no op can point at the discarded member).
                    var target = member;
                    yield return Op(memberPath, "Name", member.MemberName,
                        v => TomPropertyApplier.RenameMember(target, v));
                }

                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(memberPath, member.Annotations))
                        yield return op;
                }
            }

            // Table-permission names are absent by design: TOM derives them from the referenced
            // table, so renaming the table (covered above) is the only way they change.
            foreach (var permission in role.TablePermissions)
            {
                var permissionPath = $"{path}/{Segment(permission.Name)}";
                if (In("expressions"))
                    yield return Op(permissionPath, "FilterExpression", permission.FilterExpression, v => permission.FilterExpression = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(permissionPath, permission.Annotations))
                        yield return op;
                }
            }
        }

        if (In("annotations"))
        {
            foreach (var relationship in _database.Model.Relationships)
            {
                foreach (var op in AnnotationOps($"Relationships/{Segment(relationship.Name)}", relationship.Annotations))
                    yield return op;
            }
        }

        foreach (var perspective in _database.Model.Perspectives)
        {
            var path = $"Perspectives/{Segment(perspective.Name)}";
            if (In("names"))
                yield return Op(path, "Name", perspective.Name, v => perspective.Name = v);
            if (In("descriptions"))
                yield return Op(path, "Description", perspective.Description, v => perspective.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, perspective.Annotations))
                    yield return op;
            }
        }

        foreach (var culture in _database.Model.Cultures)
        {
            var path = $"Cultures/{Segment(culture.Name)}";
            if (In("names"))
                yield return Op(path, "Name", culture.Name, v => culture.Name = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, culture.Annotations))
                    yield return op;
            }
        }

        foreach (var dataSource in _database.Model.DataSources)
        {
            var path = $"DataSources/{Segment(dataSource.Name)}";
            if (In("names"))
                yield return Op(path, "Name", dataSource.Name, v => dataSource.Name = v);
            if (In("descriptions"))
                yield return Op(path, "Description", dataSource.Description, v => dataSource.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, dataSource.Annotations))
                    yield return op;
            }
        }

        foreach (var expression in _database.Model.Expressions)
        {
            var path = $"Expressions/{Segment(expression.Name)}";
            if (In("names"))
                yield return Op(path, "Name", expression.Name, v => expression.Name = v);
            if (In("expressions"))
                yield return Op(path, "Expression", expression.Expression, v => expression.Expression = v);
            if (In("descriptions"))
                yield return Op(path, "Description", expression.Description, v => expression.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, expression.Annotations))
                    yield return op;
            }
        }

        foreach (var function in _database.Model.Functions)
        {
            var path = $"Functions/{Segment(function.Name)}";
            if (In("names"))
                yield return Op(path, "Name", function.Name, v => function.Name = v);
            if (In("expressions"))
                yield return Op(path, "Expression", function.Expression, v => function.Expression = v);
            if (In("descriptions"))
                yield return Op(path, "Description", function.Description, v => function.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, function.Annotations))
                    yield return op;
            }
        }
    }

    private static ReplaceOperation ReplaceProperty(
        string objectPath,
        string property,
        string? value,
        Action<string> apply,
        ModelReplaceRequest request)
    {
        var before = value ?? "";
        var after = ReplaceValue(before, request);
        return new ReplaceOperation(
            new ModelReplacePreview(objectPath, property, before, after),
            apply);
    }

    private static string ReplaceValue(string value, ModelReplaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Pattern))
            return value;

        if (request.Regex)
        {
            var options = request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            return Regex.Replace(value, request.Pattern, request.Replacement, options);
        }

        var comparison = request.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var index = value.IndexOf(request.Pattern, comparison);
        if (index < 0)
            return value;

        var result = new System.Text.StringBuilder();
        var start = 0;
        while (index >= 0)
        {
            result.Append(value, start, index - start);
            result.Append(request.Replacement);
            start = index + request.Pattern.Length;
            index = value.IndexOf(request.Pattern, start, comparison);
        }

        result.Append(value, start, value.Length - start);
        return result.ToString();
    }

    private static string NormalizeReplaceScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "all";

        return scope.Trim().ToLowerInvariant() switch
        {
            "name" or "names" => "names",
            "expression" or "expressions" => "expressions",
            "description" or "descriptions" => "descriptions",
            "displayfolder" or "displayfolders" or "display-folders" => "displayfolders",
            "formatstring" or "formatstrings" or "format-strings" => "formatstrings",
            "annotation" or "annotations" => "annotations",
            "all" => "all",
            // An unknown scope would match no Build*ReplaceOperations branch and silently
            // replace nothing; reject it instead.
            _ => throw new ArgumentException(
                $"Unknown replace scope: '{scope}'. Known values: names, expressions, descriptions, displayFolders, formatStrings, annotations, all.")
        };
    }

    private sealed record ReplaceOperation(
        ModelReplacePreview Preview,
        Action<string> Apply);
}
