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
    internal static void ApplyProperties(TomWriter w, object target, IReadOnlyList<ModelPropertyAssignment> properties)
    {
        foreach (var property in properties)
            ApplyProperty(w, target, property);
    }

    internal static void ApplyProperty(TomWriter w, object target, ModelPropertyAssignment assignment)
    {
        // Annotation names are case-sensitive and their values are opaque (often JSON), so handle
        // them before the property name is normalized/lowercased.
        if (TryApplyAnnotation(w, target, assignment) || TryApplyTranslation(w, target, assignment))
            return;

        var property = TomMutationPaths.NormalizeProperty(assignment.Property);
        var value = assignment.Value;

        switch (target)
        {
            case Database database:
                ApplyModelRootProperty(w, database, property, value, assignment.Property);
                return;
            case Table table:
                ApplyTableProperty(w, table, property, value, assignment.Property);
                return;
            case Measure measure:
                ApplyMeasureProperty(w, measure, property, value, assignment.Property);
                return;
            case Column column:
                ApplyColumnProperty(w, column, property, value, assignment.Property);
                return;
            case Partition partition:
                ApplyPartitionProperty(w, partition, property, value, assignment.Property);
                return;
            case ModelRole role:
                ApplyRoleProperty(w, role, property, value, assignment.Property);
                return;
            case Hierarchy hierarchy:
                ApplyHierarchyProperty(w, hierarchy, property, value, assignment.Property);
                return;
            case Level level:
                ApplyLevelProperty(w, level, property, value, assignment.Property);
                return;
            case Calendar calendar:
                ApplyNameDescription(property, value, assignment.Property,
                    n => w.Set(calendar, p => p.Name, n), d => w.Set(calendar, p => p.Description, d));
                return;
            case NamedExpression expression:
                ApplyNamedExpressionProperty(w, expression, property, value, assignment.Property);
                return;
            case Function function:
                ApplyFunctionProperty(w, function, property, value, assignment.Property);
                return;
            case CalculationItem item:
                ApplyCalculationItemProperty(w, item, property, value, assignment.Property);
                return;
            case Perspective perspective:
                ApplyNameDescription(property, value, assignment.Property,
                    n => w.Set(perspective, p => p.Name, n), d => w.Set(perspective, p => p.Description, d));
                return;
            case Culture culture:
                if (property is not "name")
                    throw new NotSupportedException($"Setting '{assignment.Property}' is not supported for cultures.");
                w.Set(culture, p => p.Name, value);
                return;
            case DataSource dataSource:
                ApplyDataSourceProperty(w, dataSource, property, value, assignment.Property);
                return;
            case KPI kpi:
                ApplyKpiProperty(w, kpi, property, value, assignment.Property);
                return;
            case TablePermission permission:
                ApplyTablePermissionProperty(w, permission, property, value, assignment.Property);
                return;
            case ModelRoleMember member:
                ApplyMemberProperty(w, member, property, value, assignment.Property);
                return;
            case SingleColumnRelationship relationship:
                ApplyRelationshipProperty(w, relationship, property, value, assignment.Property);
                return;
            default:
                throw new NotSupportedException(
                    $"Setting '{assignment.Property}' is not supported for {target.GetType().Name} objects.");
        }
    }

    internal static void ApplyExpressionEdit(TomWriter w, object target, ModelExpressionEdit edit)
    {
        var isMainExpression = edit.Property == "Expression";
        switch (target)
        {
            case Measure measure:
                ApplyMeasureExpressionEdit(w, measure, edit);
                break;
            case CalculatedColumn column when isMainExpression:
                w.Set(column, p => p.Expression, edit.Value);
                break;
            case CalculationItem item when isMainExpression:
                w.Set(item, p => p.Expression, edit.Value);
                break;
            case Function function when isMainExpression:
                w.Set(function, p => p.Expression, edit.Value);
                break;
            case Partition { Source: CalculatedPartitionSource source } when isMainExpression:
                w.Set(source, p => p.Expression, edit.Value);
                break;
            case Table { DefaultDetailRowsDefinition: { } detailRows } when edit.Property == "DefaultDetailRowsExpression":
                w.Set(detailRows, p => p.Expression, edit.Value);
                break;
            default:
                throw new NotSupportedException(
                    $"Cannot rewrite '{edit.Property}' on {target.GetType().Name} ({edit.Path}).");
        }
    }

    private static void ApplyMeasureExpressionEdit(TomWriter w, Measure measure, ModelExpressionEdit edit)
    {
        switch (edit.Property)
        {
            case "Expression":
                w.Set(measure, p => p.Expression, edit.Value);
                break;
            case "DetailRowsExpression" when measure.DetailRowsDefinition is { } detailRows:
                w.Set(detailRows, p => p.Expression, edit.Value);
                break;
            case "FormatStringExpression" when measure.FormatStringDefinition is { } formatString:
                w.Set(formatString, p => p.Expression, edit.Value);
                break;
            case "KpiTargetExpression" when measure.KPI is { } kpi:
                w.Set(kpi, p => p.TargetExpression, edit.Value);
                break;
            case "KpiStatusExpression" when measure.KPI is { } kpi:
                w.Set(kpi, p => p.StatusExpression, edit.Value);
                break;
            case "KpiTrendExpression" when measure.KPI is { } kpi:
                w.Set(kpi, p => p.TrendExpression, edit.Value);
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
    private static bool TryApplyAnnotation(TomWriter w, object target, ModelPropertyAssignment assignment)
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
                w.Detach(annotations, existing);
            return true;
        }

        if (existing is not null)
            w.Set(existing, p => p.Value, assignment.Value);
        else
            w.Attach(annotations, new Annotation { Name = name, Value = assignment.Value });

        return true;
    }

    /// <summary>
    /// Handles a <c>translation:&lt;culture&gt;/&lt;property&gt;</c> assignment by setting or
    /// replacing the object's translation in that culture, or removing it when the value is empty.
    /// The culture must already exist, so a mistyped culture name cannot create a new culture.
    /// Returns false when the property is not a translation.
    /// </summary>
    private static bool TryApplyTranslation(TomWriter w, object target, ModelPropertyAssignment assignment)
    {
        if (!assignment.Property.StartsWith(PropertyBagKeys.TranslationPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = assignment.Property[PropertyBagKeys.TranslationPrefix.Length..];
        var slash = spec.LastIndexOf('/');
        var cultureName = slash > 0 ? spec[..slash].Trim() : "";
        var propertyName = slash > 0 ? spec[(slash + 1)..].Trim() : "";
        if (cultureName.Length == 0 || propertyName.Length == 0)
            throw new ArgumentException(
                $"'{assignment.Property}' must be translation:<culture>/<property>, for example translation:da-DK/caption.");

        var property = TomMutationPaths.NormalizeProperty(propertyName) switch
        {
            "caption" or "name" => TranslatedProperty.Caption,
            "description" => TranslatedProperty.Description,
            "displayfolder" => TranslatedProperty.DisplayFolder,
            _ => throw new NotSupportedException(
                $"Cannot translate '{propertyName}'. Translatable properties: caption, description, displayFolder.")
        };

        var (translatable, model, kindPlural, hasDisplayFolder) = target switch
        {
            Database database => ((MetadataObject)database.Model, database.Model, "the model root", false),
            Table table => (table, table.Model, "tables", false),
            Column column => (column, column.Table.Model, "columns", true),
            Measure measure => (measure, measure.Table.Model, "measures", true),
            Hierarchy hierarchy => (hierarchy, hierarchy.Table.Model, "hierarchies", true),
            Level level => (level, level.Hierarchy.Table.Model, "levels", false),
            _ => throw new NotSupportedException(
                $"Translations are not supported for {target.GetType().Name} objects; translate tables, columns, measures, hierarchies, levels, or the model root (.).")
        };
        if (property == TranslatedProperty.DisplayFolder && !hasDisplayFolder)
            throw new NotSupportedException(
                $"Cannot translate displayFolder on {kindPlural}: only measures, columns, and hierarchies have display folders.");

        var culture = model.Cultures.FirstOrDefault(c => string.Equals(c.Name, cultureName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Culture '{cultureName}' does not exist. Add it first: tx add Cultures/{cultureName} -t Culture");

        var existing = culture.ObjectTranslations[translatable, property];
        if (string.IsNullOrEmpty(assignment.Value))
        {
            if (existing is not null)
                w.Detach(culture.ObjectTranslations, existing);
        }
        else if (existing is not null)
        {
            w.Set(existing, p => p.Value, assignment.Value);
        }
        else
        {
            w.Attach(culture.ObjectTranslations, new ObjectTranslation
            {
                Object = translatable,
                Property = property,
                Value = assignment.Value
            });
        }

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
        ModelRoleMember member => member.Annotations,
        TablePermission permission => permission.Annotations,
        Hierarchy hierarchy => hierarchy.Annotations,
        Relationship relationship => relationship.Annotations,
        _ => throw new NotSupportedException($"Setting annotations is not supported for {target.GetType().Name}.")
    };

    /// <summary>
    /// The model root (the "." path resolves to the <see cref="Database"/>): database-level
    /// compatibility plus the TOM <c>Model</c> scalars. The root is not a snapshot object,
    /// so get reads these back through the snapshot's model-level properties bag.
    /// </summary>
    private static void ApplyModelRootProperty(TomWriter w, Database database, string property, string value, string displayName)
    {
        var model = database.Model;
        switch (property)
        {
            case "database.compatibilitylevel":
            case "compatibilitylevel":
                w.Set(database, p => p.CompatibilityLevel, ParseInt(value, displayName));
                break;
            case "description":
                w.Set(model, p => p.Description, value);
                break;
            case "culture":
                w.Set(model, p => p.Culture, value);
                break;
            case "collation":
                w.Set(model, p => p.Collation, value);
                break;
            case "discourageimplicitmeasures":
                w.Set(model, p => p.DiscourageImplicitMeasures, ParseBool(value, displayName));
                break;
            case "discouragecompositemodels":
                w.Set(model, p => p.DiscourageCompositeModels, ParseBool(value, displayName));
                break;
            // DiscourageReportMeasures is deliberately absent: TOM's setter demands the
            // internal-only compatibility sentinel, so the hint must not advertise it.
            case "defaultmode":
                w.Set(model, p => p.DefaultMode, ParseEnum<ModeType>(value, displayName));
                break;
            case "defaultdataview":
                w.Set(model, p => p.DefaultDataView, ParseEnum<DataViewType>(value, displayName));
                break;
            case "maxparallelismperquery":
                w.Set(model, p => p.MaxParallelismPerQuery, ParseInt(value, displayName));
                break;
            case "maxparallelismperrefresh":
                w.Set(model, p => p.MaxParallelismPerRefresh, ParseInt(value, displayName));
                break;
            case "sourcequeryculture":
                w.Set(model, p => p.SourceQueryCulture, value);
                break;
            case "forceuniquenames":
                w.Set(model, p => p.ForceUniqueNames, ParseBool(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "the model root", ModelObjectKind.Model);
        }
    }

    private static void ApplyTableProperty(TomWriter w, Table table, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(table, p => p.Name, value);
                break;
            case "description":
                w.Set(table, p => p.Description, value);
                break;
            case "ishidden":
                w.Set(table, p => p.IsHidden, ParseBool(value, displayName));
                break;
            case "datacategory":
                w.Set(table, p => p.DataCategory, value);
                break;
            case "lineagetag":
                w.Set(table, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(table, p => p.SourceLineageTag, value);
                break;
            case "isprivate":
                w.Set(table, p => p.IsPrivate, ParseBool(value, displayName));
                break;
            case "excludefrommodelrefresh":
                w.Set(table, p => p.ExcludeFromModelRefresh, ParseBool(value, displayName));
                break;
            case "excludefromautomaticaggregations":
                w.Set(table, p => p.ExcludeFromAutomaticAggregations, ParseBool(value, displayName));
                break;
            case "alternatesourceprecedence":
                w.Set(table, p => p.AlternateSourcePrecedence, ParseInt(value, displayName));
                break;
            case "showasvariationsonly":
                w.Set(table, p => p.ShowAsVariationsOnly, ParseBool(value, displayName));
                break;
            case "systemmanaged":
                w.Set(table, p => p.SystemManaged, ParseBool(value, displayName));
                break;
            case "directlakeindexingbehavior":
                w.Set(table, p => p.DirectLakeIndexingBehavior, ParseEnum<DirectLakeIndexingBehavior>(value, displayName));
                break;
            // Precedence lives on the CalculationGroup member of a calculation-group table,
            // so the hint hides it from plain tables (partition's source-bound precedent).
            case "precedence" when table.CalculationGroup is { } calculationGroup:
                w.Set(calculationGroup, p => p.Precedence, ParseInt(value, displayName));
                break;
            case "precedence":
                throw new NotSupportedException(
                    $"Setting '{displayName}' is only supported for calculation group tables.");
            default:
                var excludes = new List<string>();
                if (table.CalculationGroup is null)
                    excludes.Add("precedence");
                throw UnsupportedProperty(displayName, "tables", ModelObjectKind.Table, exclude: excludes);
        }
    }

    private static void ApplyMeasureProperty(TomWriter w, Measure measure, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(measure, p => p.Name, value);
                break;
            case "description":
                w.Set(measure, p => p.Description, value);
                break;
            case "expression":
                w.Set(measure, p => p.Expression, value);
                break;
            case "formatstring":
                w.Set(measure, p => p.FormatString, value);
                break;
            case "displayfolder":
                w.Set(measure, p => p.DisplayFolder, value);
                break;
            case "ishidden":
                w.Set(measure, p => p.IsHidden, ParseBool(value, displayName));
                break;
            case "datacategory":
                w.Set(measure, p => p.DataCategory, value);
                break;
            case "lineagetag":
                w.Set(measure, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(measure, p => p.SourceLineageTag, value);
                break;
            case "issimplemeasure":
                w.Set(measure, p => p.IsSimpleMeasure, ParseBool(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "measures", ModelObjectKind.Measure);
        }
    }

    private static void ApplyColumnProperty(TomWriter w, Column column, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(column, p => p.Name, value);
                break;
            case "description":
                w.Set(column, p => p.Description, value);
                break;
            case "expression" when column is CalculatedColumn calculated:
                w.Set(calculated, p => p.Expression, value);
                break;
            case "formatstring":
                w.Set(column, p => p.FormatString, value);
                break;
            case "displayfolder":
                w.Set(column, p => p.DisplayFolder, value);
                break;
            case "ishidden":
                w.Set(column, p => p.IsHidden, ParseBool(value, displayName));
                break;
            case "sourcecolumn":
                ApplySourceColumn(w, column, value, displayName);
                break;
            case "datacategory":
                w.Set(column, p => p.DataCategory, value);
                break;
            case "lineagetag":
                w.Set(column, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(column, p => p.SourceLineageTag, value);
                break;
            case "sourceprovidertype":
                w.Set(column, p => p.SourceProviderType, value);
                break;
            case "iskey":
                w.Set(column, p => p.IsKey, ParseBool(value, displayName));
                break;
            case "isnullable":
                w.Set(column, p => p.IsNullable, ParseBool(value, displayName));
                break;
            case "isunique":
                w.Set(column, p => p.IsUnique, ParseBool(value, displayName));
                break;
            case "isavailableinmdx":
                w.Set(column, p => p.IsAvailableInMDX, ParseBool(value, displayName));
                break;
            case "keepuniquerows":
                w.Set(column, p => p.KeepUniqueRows, ParseBool(value, displayName));
                break;
            case "isdefaultlabel":
                w.Set(column, p => p.IsDefaultLabel, ParseBool(value, displayName));
                break;
            case "isdefaultimage":
                w.Set(column, p => p.IsDefaultImage, ParseBool(value, displayName));
                break;
            case "isdatatypeinferred":
                w.Set(column, p => p.IsDataTypeInferred, ParseBool(value, displayName));
                break;
            case "tabledetailposition":
                w.Set(column, p => p.TableDetailPosition, ParseInt(value, displayName));
                break;
            case "displayordinal":
                w.Set(column, p => p.DisplayOrdinal, ParseInt(value, displayName));
                break;
            case "datatype":
                w.Set(column, p => p.DataType, ParseDataType(value, displayName));
                break;
            case "summarizeby":
                w.Set(column, p => p.SummarizeBy, ParseEnum<AggregateFunction>(value, displayName));
                break;
            case "alignment":
                w.Set(column, p => p.Alignment, ParseEnum<Alignment>(value, displayName));
                break;
            case "encodinghint":
                w.Set(column, p => p.EncodingHint, ParseEnum<EncodingHintType>(value, displayName));
                break;
            case "sortbycolumn":
                ApplySortByColumn(w, column, value, displayName);
                break;
            default:
                throw UnsupportedProperty(displayName, "columns", ModelObjectKind.Column);
        }
    }

    /// <summary>
    /// <c>SourceColumn</c> exists only on data and calculated-table columns; a calculated
    /// column's values come from its DAX expression instead.
    /// </summary>
    private static void ApplySourceColumn(TomWriter w, Column column, string value, string displayName)
    {
        switch (column)
        {
            case DataColumn dataColumn:
                w.Set(dataColumn, p => p.SourceColumn, value);
                break;
            case CalculatedTableColumn tableColumn:
                w.Set(tableColumn, p => p.SourceColumn, value);
                break;
            default:
                throw new NotSupportedException(
                    $"Setting '{displayName}' is not supported for calculated columns; set 'expression' instead.");
        }
    }

    /// <summary>Resolves the sort-by column by name within the same table; empty clears it.</summary>
    private static void ApplySortByColumn(TomWriter w, Column column, string value, string displayName)
    {
        if (string.IsNullOrEmpty(value))
        {
            w.Set(column, p => p.SortByColumn, null);
            return;
        }

        if (column.Table is not { } table)
            throw new NotSupportedException($"Cannot set '{displayName}' on a column that is not attached to a table.");

        w.Set(column, p => p.SortByColumn, table.Columns.Find(value)
            ?? throw new ArgumentException(
                $"Column '{value}' does not exist in table '{table.Name}'; '{displayName}' must name a column in the same table."));
    }

    /// <summary>Parses a data type, accepting the same friendly aliases the catalog normalizes (e.g. <c>bool</c>).</summary>
    private static DataType ParseDataType(string value, string displayName)
    {
        var normalized = ModelPropertyCatalog.NormalizeDataType(value);
        return ParseEnum<DataType>(normalized.Length == 0 ? value : normalized, displayName);
    }

    private static void ApplyPartitionProperty(TomWriter w, Partition partition, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(partition, p => p.Name, value);
                break;
            case "description":
                w.Set(partition, p => p.Description, value);
                break;
            case "mode":
                w.Set(partition, p => p.Mode, ParseEnum<ModeType>(value, displayName));
                break;
            case "dataview":
                w.Set(partition, p => p.DataView, ParseEnum<DataViewType>(value, displayName));
                break;
            case "retaindatatillforcecalculate" when partition.Source is CalculatedPartitionSource calculated:
                w.Set(calculated, p => p.RetainDataTillForceCalculate, ParseBool(value, displayName));
                break;
            case "retaindatatillforcecalculate":
                throw new NotSupportedException(
                    "Setting 'retainDataTillForceCalculate' is only supported for partitions with a calculated source; " +
                    $"this partition's source is {partition.SourceType}.");
            case "querygroup":
                ApplyQueryGroup(w, partition, value, displayName);
                break;
            case "expression" when partition.Source is MPartitionSource m:
                w.Set(m, p => p.Expression, value);
                break;
            case "expression":
                throw new NotSupportedException(
                    "Setting 'expression' is only supported for partitions with an M source; " +
                    $"this partition's source is {partition.SourceType}.");
            default:
                // Source-bound properties stay out of the hint for partitions that cannot take
                // them: 'expression' needs an M source, 'retainDataTillForceCalculate' a
                // calculated source.
                var excludes = new List<string>();
                if (partition.Source is not MPartitionSource)
                    excludes.Add("expression");
                if (partition.Source is not CalculatedPartitionSource)
                    excludes.Add("retainDataTillForceCalculate");
                throw UnsupportedProperty(displayName, "partitions", ModelObjectKind.Partition, exclude: excludes);
        }
    }

    /// <summary>Resolves the query group by name on the model; empty clears it.</summary>
    private static void ApplyQueryGroup(TomWriter w, Partition partition, string value, string displayName)
    {
        if (string.IsNullOrEmpty(value))
        {
            w.Set(partition, p => p.QueryGroup, null);
            return;
        }

        if (partition.Model is not { } model)
            throw new NotSupportedException($"Cannot set '{displayName}' on a partition that is not attached to a model.");

        w.Set(partition, p => p.QueryGroup, model.QueryGroups.Find(value)
            ?? throw new ArgumentException(
                $"Query group '{value}' does not exist in the model; '{displayName}' must name an existing query group."));
    }

    private static NotSupportedException UnsupportedProperty(
        string displayName, string kindPlural, ModelObjectKind kind, IReadOnlyList<string>? exclude = null)
    {
        var writable = ModelPropertyCatalog.WritableTokens(kind)
            .Where(t => exclude is null || !exclude.Contains(t))
            .ToList();
        var hint = writable.Count > 0
            ? $" Writable properties: {string.Join(", ", writable)}, {PropertyBagKeys.AnnotationPrefix}<name>."
            : "";
        return new NotSupportedException($"Setting '{displayName}' is not supported for {kindPlural}.{hint}");
    }

    private static void ApplyRoleProperty(TomWriter w, ModelRole role, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(role, p => p.Name, value);
                break;
            case "description":
                w.Set(role, p => p.Description, value);
                break;
            case "modelpermission":
                w.Set(role, p => p.ModelPermission, ParseEnum<ModelPermission>(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "roles", ModelObjectKind.Role);
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

    private static void ApplyHierarchyProperty(TomWriter w, Hierarchy hierarchy, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(hierarchy, p => p.Name, value);
                break;
            case "description":
                w.Set(hierarchy, p => p.Description, value);
                break;
            case "displayfolder":
                w.Set(hierarchy, p => p.DisplayFolder, value);
                break;
            case "ishidden":
                w.Set(hierarchy, p => p.IsHidden, ParseBool(value, displayName));
                break;
            case "hidemembers":
                w.Set(hierarchy, p => p.HideMembers, ParseEnum<HierarchyHideMembersType>(value, displayName));
                break;
            case "lineagetag":
                w.Set(hierarchy, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(hierarchy, p => p.SourceLineageTag, value);
                break;
            default:
                throw UnsupportedProperty(displayName, "hierarchies", ModelObjectKind.Hierarchy);
        }
    }

    private static void ApplyLevelProperty(TomWriter w, Level level, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(level, p => p.Name, value);
                break;
            case "description":
                w.Set(level, p => p.Description, value);
                break;
            case "ordinal":
                w.Set(level, p => p.Ordinal, ParseInt(value, displayName));
                break;
            case "lineagetag":
                w.Set(level, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(level, p => p.SourceLineageTag, value);
                break;
            default:
                throw UnsupportedProperty(displayName, "levels", ModelObjectKind.Level);
        }
    }

    private static void ApplyNamedExpressionProperty(TomWriter w, NamedExpression expression, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(expression, p => p.Name, value);
                break;
            case "description":
                w.Set(expression, p => p.Description, value);
                break;
            case "expression":
                w.Set(expression, p => p.Expression, value);
                break;
            case "kind":
                w.Set(expression, p => p.Kind, ParseEnum<ExpressionKind>(value, displayName));
                break;
            case "remoteparametername":
                w.Set(expression, p => p.RemoteParameterName, value);
                break;
            case "lineagetag":
                w.Set(expression, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(expression, p => p.SourceLineageTag, value);
                break;
            default:
                throw UnsupportedProperty(displayName, "expressions", ModelObjectKind.Expression);
        }
    }

    private static void ApplyFunctionProperty(TomWriter w, Function function, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(function, p => p.Name, value);
                break;
            case "description":
                w.Set(function, p => p.Description, value);
                break;
            case "expression":
                w.Set(function, p => p.Expression, value);
                break;
            case "ishidden":
                w.Set(function, p => p.IsHidden, ParseBool(value, displayName));
                break;
            case "lineagetag":
                w.Set(function, p => p.LineageTag, value);
                break;
            case "sourcelineagetag":
                w.Set(function, p => p.SourceLineageTag, value);
                break;
            default:
                throw UnsupportedProperty(displayName, "functions", ModelObjectKind.Function);
        }
    }

    private static void ApplyCalculationItemProperty(TomWriter w, CalculationItem item, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(item, p => p.Name, value);
                break;
            case "description":
                w.Set(item, p => p.Description, value);
                break;
            case "expression":
                w.Set(item, p => p.Expression, value);
                break;
            case "ordinal":
                w.Set(item, p => p.Ordinal, ParseInt(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "calculation items", ModelObjectKind.CalculationItem);
        }
    }

    private static void ApplyDataSourceProperty(TomWriter w, DataSource dataSource, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(dataSource, p => p.Name, value);
                break;
            case "description":
                w.Set(dataSource, p => p.Description, value);
                break;
            case "maxconnections":
                w.Set(dataSource, p => p.MaxConnections, ParseInt(value, displayName));
                break;
            // Provider-only and structured-only fields guard on the source kind, and the
            // hint trims the tokens the targeted source cannot take.
            case "provider" when dataSource is ProviderDataSource provider:
                w.Set(provider, p => p.Provider, value);
                break;
            case "impersonationmode" when dataSource is ProviderDataSource provider:
                w.Set(provider, p => p.ImpersonationMode, ParseEnum<ImpersonationMode>(value, displayName));
                break;
            case "isolation" when dataSource is ProviderDataSource provider:
                w.Set(provider, p => p.Isolation, ParseEnum<DatasourceIsolation>(value, displayName));
                break;
            case "timeout" when dataSource is ProviderDataSource provider:
                // TOM stores the provider timeout as whole seconds.
                w.Set(provider, p => p.Timeout, ParseInt(value, displayName));
                break;
            case "contextexpression" when dataSource is StructuredDataSource structured:
                w.Set(structured, p => p.ContextExpression, value);
                break;
            // Credentials are secrets, and secrets are never accepted via argv
            // (docs/cli-ux-guidelines.md); scripted edits are the escape hatch.
            case "connectionstring":
            case "connectiondetails":
            case "credential":
            case "account":
            case "password":
                throw new NotSupportedException(
                    $"Setting '{displayName}' is not supported: secrets are never accepted via argv. "
                    + "Edit the source file to change credentials.");
            default:
                var excludes = new List<string>();
                if (dataSource is not ProviderDataSource)
                {
                    excludes.Add("impersonationMode");
                    excludes.Add("isolation");
                    excludes.Add("timeout");
                }
                if (dataSource is not StructuredDataSource)
                    excludes.Add("contextExpression");
                throw UnsupportedProperty(displayName, "data sources", ModelObjectKind.DataSource, exclude: excludes);
        }
    }

    private static void ApplyKpiProperty(TomWriter w, KPI kpi, string property, string value, string displayName)
    {
        switch (property)
        {
            case "description":
                w.Set(kpi, p => p.Description, value);
                break;
            case "targetexpression":
                w.Set(kpi, p => p.TargetExpression, value);
                break;
            case "targetformatstring":
                w.Set(kpi, p => p.TargetFormatString, value);
                break;
            case "statusexpression":
                w.Set(kpi, p => p.StatusExpression, value);
                break;
            case "trendexpression":
                w.Set(kpi, p => p.TrendExpression, value);
                break;
            case "statusgraphic":
                w.Set(kpi, p => p.StatusGraphic, value);
                break;
            case "trendgraphic":
                w.Set(kpi, p => p.TrendGraphic, value);
                break;
            case "statusdescription":
                w.Set(kpi, p => p.StatusDescription, value);
                break;
            case "targetdescription":
                w.Set(kpi, p => p.TargetDescription, value);
                break;
            case "trenddescription":
                w.Set(kpi, p => p.TrendDescription, value);
                break;
            default:
                throw UnsupportedProperty(displayName, "KPIs", ModelObjectKind.Kpi);
        }
    }

    private static void ApplyTablePermissionProperty(TomWriter w, TablePermission permission, string property, string value, string displayName)
    {
        switch (property)
        {
            // No 'name' case: TOM derives the permission's name from its table and rejects the
            // assignment, so the catalog does not advertise it and the hint falls through here.
            case "filterexpression":
                w.Set(permission, p => p.FilterExpression, value);
                break;
            case "metadatapermission":
                w.Set(permission, p => p.MetadataPermission, ParseEnum<MetadataPermission>(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "table permissions", ModelObjectKind.TablePermission);
        }
    }

    private static void ApplyMemberProperty(TomWriter w, ModelRoleMember member, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
            case "membername":
                ReplaceMember(w, member, newName: value);
                break;
            case "memberid":
                ReplaceMember(w, member, memberId: value);
                break;
            // Identity fields (MemberName, MemberID, IdentityProvider, MemberType) are all
            // frozen by TOM once the member is attached, so every one of them replaces the
            // member. The provider fields exist only on ExternalModelRoleMember; a Windows
            // member gets a tailored error and those tokens stay out of its hint.
            case "identityprovider" when member is ExternalModelRoleMember:
                ReplaceMember(w, member, identityProvider: value);
                break;
            case "membertype" when member is ExternalModelRoleMember external:
                ReplaceMember(w, member, memberType: ParseEnum<RoleMemberType>(value, displayName));
                break;
            case "identityprovider":
            case "membertype":
                throw new NotSupportedException(
                    $"Setting '{displayName}' is only supported for external role members; this member is a Windows member.");
            default:
                var excludes = new List<string>();
                if (member is not ExternalModelRoleMember)
                {
                    excludes.Add("identityProvider");
                    excludes.Add("memberType");
                }
                throw UnsupportedProperty(displayName, "role members", ModelObjectKind.RoleMember, exclude: excludes);
        }
    }

    /// <summary>
    /// TOM freezes every identity field of an attached <c>ModelRoleMember</c> (member name and
    /// ID, and on external members the identity provider and member type), so changing any of
    /// them must replace the member with an equivalent one. Annotation values carry over
    /// (as clones — TOM refuses to reattach removed objects). Also used by TomTextReplacer.
    /// </summary>
    internal static ModelRoleMember ReplaceMember(
        TomWriter w, ModelRoleMember member,
        string? newName = null,
        string? memberId = null,
        string? identityProvider = null,
        RoleMemberType? memberType = null)
    {
        if (member.Role is not { } role)
            throw new NotSupportedException("Cannot replace a role member that is not attached to a role.");

        ModelRoleMember renamed = member switch
        {
            ExternalModelRoleMember external => new ExternalModelRoleMember
            {
                MemberName = newName ?? external.MemberName,
                MemberID = memberId ?? external.MemberID,
                IdentityProvider = identityProvider ?? external.IdentityProvider,
                MemberType = memberType ?? external.MemberType
            },
            _ => new WindowsModelRoleMember
            {
                MemberName = newName ?? member.MemberName,
                MemberID = memberId ?? member.MemberID
            }
        };

        // Clone the annotations: TOM's change tracker refuses to reattach removed objects.
        foreach (var annotation in member.Annotations)
            w.Attach(renamed.Annotations, new Annotation { Name = annotation.Name, Value = annotation.Value });

        // The replacement keeps the member's ID, so the swap reports as a change to one object.
        w.Rebind(member, renamed);
        w.Detach(role.Members, member);
        w.Attach(role.Members, renamed);
        return renamed;
    }

    private static void ApplyRelationshipProperty(TomWriter w, SingleColumnRelationship relationship, string property, string value, string displayName)
    {
        switch (property)
        {
            case "name":
                w.Set(relationship, p => p.Name, value);
                break;
            case "isactive":
                w.Set(relationship, p => p.IsActive, ParseBool(value, displayName));
                break;
            case "crossfilteringbehavior":
                w.Set(relationship, p => p.CrossFilteringBehavior, ParseEnum<CrossFilteringBehavior>(value, displayName));
                break;
            case "fromcardinality":
                w.Set(relationship, p => p.FromCardinality, ParseEnum<RelationshipEndCardinality>(value, displayName));
                break;
            case "tocardinality":
                w.Set(relationship, p => p.ToCardinality, ParseEnum<RelationshipEndCardinality>(value, displayName));
                break;
            case "securityfilteringbehavior":
                w.Set(relationship, p => p.SecurityFilteringBehavior, ParseEnum<SecurityFilteringBehavior>(value, displayName));
                break;
            case "relyonreferentialintegrity":
                w.Set(relationship, p => p.RelyOnReferentialIntegrity, ParseBool(value, displayName));
                break;
            case "joinondatebehavior":
                w.Set(relationship, p => p.JoinOnDateBehavior, ParseEnum<DateTimeRelationshipBehavior>(value, displayName));
                break;
            default:
                throw UnsupportedProperty(displayName, "relationships", ModelObjectKind.Relationship);
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
