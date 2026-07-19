using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Provider.Tom;

/// <summary>
/// Applies property assignments and expression edits to resolved TOM objects: annotation
/// handling, the per-type property dispatch, and the value parsers. Property support and
/// error hints follow <see cref="ModelPropertyCatalog"/>.
/// </summary>
internal static class TomPropertyApplier
{
    internal static void ApplyProperties(object target, IReadOnlyList<ModelPropertyAssignment> properties)
    {
        foreach (var property in properties)
            ApplyProperty(target, property);
    }

    internal static void ApplyProperty(object target, ModelPropertyAssignment assignment)
    {
        // Annotation names are case-sensitive and their values are opaque (often JSON), so handle
        // them before the property name is normalized/lowercased.
        if (TryApplyAnnotation(target, assignment))
            return;

        var property = TomMutationPaths.NormalizeProperty(assignment.Property);
        var value = assignment.Value;

        switch (target)
        {
            case Database database when property is "database.compatibilitylevel" or "compatibilitylevel":
                database.CompatibilityLevel = ParseInt(value, assignment.Property);
                return;
            case Table table:
                ApplyTableProperty(table, property, value, assignment.Property);
                return;
            case Measure measure:
                ApplyMeasureProperty(measure, property, value, assignment.Property);
                return;
            case Column column:
                ApplyColumnProperty(column, property, value, assignment.Property);
                return;
            case Partition partition:
                ApplyPartitionProperty(partition, property, value, assignment.Property);
                return;
            case ModelRole role:
                ApplyRoleProperty(role, property, value, assignment.Property);
                return;
            case Hierarchy hierarchy:
                ApplyHierarchyProperty(hierarchy, property, value, assignment.Property);
                return;
            case Level level:
                ApplyLevelProperty(level, property, value, assignment.Property);
                return;
            case Calendar calendar:
                ApplyNameDescription(property, value, assignment.Property,
                    n => calendar.Name = n, d => calendar.Description = d);
                return;
            case NamedExpression expression:
                ApplyNamedExpressionProperty(expression, property, value, assignment.Property);
                return;
            case Function function:
                ApplyFunctionProperty(function, property, value, assignment.Property);
                return;
            case CalculationItem item:
                ApplyCalculationItemProperty(item, property, value, assignment.Property);
                return;
            case Perspective perspective:
                ApplyNameDescription(property, value, assignment.Property,
                    n => perspective.Name = n, d => perspective.Description = d);
                return;
            case Culture culture:
                if (property is not "name")
                    throw new NotSupportedException($"Setting '{assignment.Property}' is not supported for cultures.");
                culture.Name = value;
                return;
            case DataSource dataSource:
                ApplyDataSourceProperty(dataSource, property, value, assignment.Property);
                return;
            case KPI kpi:
                ApplyKpiProperty(kpi, property, value, assignment.Property);
                return;
            case TablePermission permission:
                ApplyTablePermissionProperty(permission, property, value, assignment.Property);
                return;
            case ModelRoleMember member:
                ApplyMemberProperty(member, property, value, assignment.Property);
                return;
            case SingleColumnRelationship relationship:
                ApplyRelationshipProperty(relationship, property, value, assignment.Property);
                return;
            default:
                throw new NotSupportedException(
                    $"Setting '{assignment.Property}' is not supported for {target.GetType().Name} objects.");
        }
    }

    internal static void ApplyExpressionEdit(object target, ModelExpressionEdit edit)
    {
        var isMainExpression = edit.Property == "Expression";
        switch (target)
        {
            case Measure measure:
                ApplyMeasureExpressionEdit(measure, edit);
                break;
            case CalculatedColumn column when isMainExpression:
                column.Expression = edit.Value;
                break;
            case CalculationItem item when isMainExpression:
                item.Expression = edit.Value;
                break;
            case Partition { Source: CalculatedPartitionSource source } when isMainExpression:
                source.Expression = edit.Value;
                break;
            case Table { DefaultDetailRowsDefinition: { } detailRows } when edit.Property == "DefaultDetailRowsExpression":
                detailRows.Expression = edit.Value;
                break;
            default:
                throw new NotSupportedException(
                    $"Cannot rewrite '{edit.Property}' on {target.GetType().Name} ({edit.Path}).");
        }
    }

    private static void ApplyMeasureExpressionEdit(Measure measure, ModelExpressionEdit edit)
    {
        switch (edit.Property)
        {
            case "Expression":
                measure.Expression = edit.Value;
                break;
            case "DetailRowsExpression" when measure.DetailRowsDefinition is { } detailRows:
                detailRows.Expression = edit.Value;
                break;
            case "FormatStringExpression" when measure.FormatStringDefinition is { } formatString:
                formatString.Expression = edit.Value;
                break;
            case "KpiTargetExpression" when measure.KPI is { } kpi:
                kpi.TargetExpression = edit.Value;
                break;
            case "KpiStatusExpression" when measure.KPI is { } kpi:
                kpi.StatusExpression = edit.Value;
                break;
            case "KpiTrendExpression" when measure.KPI is { } kpi:
                kpi.TrendExpression = edit.Value;
                break;
            default:
                throw new NotSupportedException(
                    $"Cannot rewrite '{edit.Property}' on measure {edit.Path}.");
        }
    }

    private const string AnnotationPrefix = "Annotation:";

    /// <summary>
    /// Handles a <c>Annotation:&lt;Name&gt;</c> assignment by setting/replacing the annotation, or
    /// removing it when the value is empty. Returns false when the property is not an annotation.
    /// </summary>
    private static bool TryApplyAnnotation(object target, ModelPropertyAssignment assignment)
    {
        if (!assignment.Property.StartsWith(AnnotationPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = assignment.Property[AnnotationPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Annotation name is required.", nameof(assignment));

        var annotations = ResolveAnnotations(target);
        var existing = annotations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

        if (string.IsNullOrEmpty(assignment.Value))
        {
            if (existing is not null)
                annotations.Remove(existing);
            return true;
        }

        if (existing is not null)
            existing.Value = assignment.Value;
        else
            annotations.Add(new Annotation { Name = name, Value = assignment.Value });

        return true;
    }

    /// <summary>
    /// Returns the annotation collection for a mutation target. Model-level annotations
    /// ("." path resolves to the <see cref="Database"/>) live on <c>Database.Model</c>.
    /// </summary>
    private static ICollection<Annotation> ResolveAnnotations(object target) => target switch
    {
        Database database => database.Model.Annotations,
        Model model => model.Annotations,
        Table table => table.Annotations,
        Column column => column.Annotations,
        Measure measure => measure.Annotations,
        Partition partition => partition.Annotations,
        ModelRole role => role.Annotations,
        Hierarchy hierarchy => hierarchy.Annotations,
        Relationship relationship => relationship.Annotations,
        _ => throw new NotSupportedException($"Setting annotations is not supported for {target.GetType().Name}.")
    };

    private static void ApplyTableProperty(Table table, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                table.Name = value;
                break;
            case "description":
                table.Description = value;
                break;
            case "ishidden":
                table.IsHidden = ParseBool(value, displayName);
                break;
            case "datacategory":
                table.DataCategory = value;
                break;
            default:
                throw UnsupportedProperty(displayName, "tables", ModelObjectKind.Table);
        }
    }

    private static void ApplyMeasureProperty(Measure measure, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                measure.Name = value;
                break;
            case "description":
                measure.Description = value;
                break;
            case "expression":
                measure.Expression = value;
                break;
            case "formatstring":
                measure.FormatString = value;
                break;
            case "displayfolder":
                measure.DisplayFolder = value;
                break;
            case "ishidden":
                measure.IsHidden = ParseBool(value, displayName);
                break;
            default:
                throw UnsupportedProperty(displayName, "measures", ModelObjectKind.Measure);
        }
    }

    private static void ApplyColumnProperty(Column column, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                column.Name = value;
                break;
            case "description":
                column.Description = value;
                break;
            case "formatstring":
                column.FormatString = value;
                break;
            case "displayfolder":
                column.DisplayFolder = value;
                break;
            case "ishidden":
                column.IsHidden = ParseBool(value, displayName);
                break;
            default:
                throw UnsupportedProperty(displayName, "columns", ModelObjectKind.Column);
        }
    }

    private static void ApplyPartitionProperty(Partition partition, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                partition.Name = value;
                break;
            case "expression" when partition.Source is MPartitionSource m:
                m.Expression = value;
                break;
            case "expression":
                throw new NotSupportedException(
                    "Setting 'expression' is only supported for partitions with an M source; " +
                    $"this partition's source is {partition.SourceType}.");
            default:
                // 'expression' is only settable on M-source partitions, so keep it out of the
                // hint for Entity/PolicyRange/Calculated partitions.
                throw UnsupportedProperty(displayName, "partitions", ModelObjectKind.Partition,
                    exclude: partition.Source is MPartitionSource ? null : "expression");
        }
    }

    private static NotSupportedException UnsupportedProperty(
        string displayName, string kindPlural, ModelObjectKind kind, string? exclude = null)
    {
        var writable = ModelPropertyCatalog.WritableTokens(kind).Where(t => t != exclude).ToList();
        var hint = writable.Count > 0
            ? $" Writable properties: {string.Join(", ", writable)}, {PropertyBagKeys.AnnotationPrefix}<name>."
            : "";
        return new NotSupportedException($"Setting '{displayName}' is not supported for {kindPlural}.{hint}");
    }

    private static void ApplyRoleProperty(ModelRole role, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                role.Name = value;
                break;
            case "description":
                role.Description = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for roles.");
        }
    }

    private static void ApplyNameDescription(string property, string value, string displayName, Action<string> setName, Action<string> setDescription)
    {
        switch (property)
        {
            case "name":
                setName(value);
                break;
            case "description":
                setDescription(value);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for this object.");
        }
    }

    private static void ApplyHierarchyProperty(Hierarchy hierarchy, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                hierarchy.Name = value;
                break;
            case "description":
                hierarchy.Description = value;
                break;
            case "displayfolder":
                hierarchy.DisplayFolder = value;
                break;
            case "ishidden":
                hierarchy.IsHidden = ParseBool(value, displayName);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for hierarchies.");
        }
    }

    private static void ApplyLevelProperty(Level level, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                level.Name = value;
                break;
            case "description":
                level.Description = value;
                break;
            case "ordinal":
                level.Ordinal = ParseInt(value, displayName);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for levels.");
        }
    }

    private static void ApplyNamedExpressionProperty(NamedExpression expression, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                expression.Name = value;
                break;
            case "description":
                expression.Description = value;
                break;
            case "expression":
                expression.Expression = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for expressions.");
        }
    }

    private static void ApplyFunctionProperty(Function function, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                function.Name = value;
                break;
            case "description":
                function.Description = value;
                break;
            case "expression":
                function.Expression = value;
                break;
            case "ishidden":
                function.IsHidden = ParseBool(value, displayName);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for functions.");
        }
    }

    private static void ApplyCalculationItemProperty(CalculationItem item, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                item.Name = value;
                break;
            case "description":
                item.Description = value;
                break;
            case "expression":
                item.Expression = value;
                break;
            case "ordinal":
                item.Ordinal = ParseInt(value, displayName);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for calculation items.");
        }
    }

    private static void ApplyDataSourceProperty(DataSource dataSource, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                dataSource.Name = value;
                break;
            case "description":
                dataSource.Description = value;
                break;
            case "connectionstring" when dataSource is ProviderDataSource provider:
                provider.ConnectionString = value;
                break;
            case "provider" when dataSource is ProviderDataSource provider:
                provider.Provider = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for data sources.");
        }
    }

    private static void ApplyKpiProperty(KPI kpi, string property, string value, string displayName)
    {
        switch (property)
        {
            case "description":
                kpi.Description = value;
                break;
            case "targetexpression":
                kpi.TargetExpression = value;
                break;
            case "targetformatstring":
                kpi.TargetFormatString = value;
                break;
            case "statusexpression":
                kpi.StatusExpression = value;
                break;
            case "trendexpression":
                kpi.TrendExpression = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for KPIs.");
        }
    }

    private static void ApplyTablePermissionProperty(TablePermission permission, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                permission.Name = value;
                break;
            case "filterexpression":
                permission.FilterExpression = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for table permissions.");
        }
    }

    private static void ApplyMemberProperty(ModelRoleMember member, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
            case "membername":
                member.MemberName = value;
                break;
            case "memberid":
                member.MemberID = value;
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for role members.");
        }
    }

    private static void ApplyRelationshipProperty(SingleColumnRelationship relationship, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                relationship.Name = value;
                break;
            case "isactive":
                relationship.IsActive = ParseBool(value, displayName);
                break;
            case "crossfilteringbehavior":
                relationship.CrossFilteringBehavior = ParseEnum<CrossFilteringBehavior>(value, displayName);
                break;
            case "fromcardinality":
                relationship.FromCardinality = ParseEnum<RelationshipEndCardinality>(value, displayName);
                break;
            case "tocardinality":
                relationship.ToCardinality = ParseEnum<RelationshipEndCardinality>(value, displayName);
                break;
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for relationships.");
        }
    }

    internal static bool ParseBool(string value, string property)
    {
        if (bool.TryParse(value, out var parsed))
            return parsed;

        if (value == "1")
            return true;
        if (value == "0")
            return false;

        throw new ArgumentException($"Value for '{property}' must be true or false.");
    }

    internal static int ParseInt(string value, string property)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;

        throw new ArgumentException($"Value for '{property}' must be an integer.");
    }

    internal static TEnum ParseEnum<TEnum>(string value, string property) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed))
            return parsed;

        throw new ArgumentException(
            $"Value for '{property}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
