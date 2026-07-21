using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Provider.Tom;

public static class TomModelSummarizer
{
    private const string PropDataType = "DataType";
    private const string PropColumnType = "ColumnType";
    private const string PropIsKey = "IsKey";
    private const string PropIsAvailableInMdx = "IsAvailableInMdx";
    private const string PropSummarizeBy = "SummarizeBy";
    private const string PropFormatString = "FormatString";
    private const string PropDisplayFolder = "DisplayFolder";
    private const string PropDataCategory = "DataCategory";
    private const string PropSortByColumn = "SortByColumn";
    private const string PropTableDataCategory = "TableDataCategory";
    private const string PropTableHasRls = "TableHasRls";
    private const string PropTableIsCalc = "TableIsCalc";
    private const string PropFromColumn = PropertyBagKeys.FromColumn;
    private const string PropToColumn = PropertyBagKeys.ToColumn;
    private const string PropFromTable = "FromTable";
    private const string PropToTable = "ToTable";
    private const string PropFromCardinality = PropertyBagKeys.FromCardinality;
    private const string PropToCardinality = PropertyBagKeys.ToCardinality;
    private const string PropCrossFilteringBehavior = PropertyBagKeys.CrossFilteringBehavior;
    private const string PropIsActive = PropertyBagKeys.IsActive;
    private const string PropPartitionSourceType = "PartitionSourceType";
    private const string PropPartitionMode = "PartitionMode";
    private const string PropPartitionDataView = "DataView";
    private const string PropPartitionQueryGroup = "QueryGroup";
    private const string PropRlsExpression = PropertyBagKeys.RlsExpression;
    private const string PropUsedInRelationships = "UsedInRelationships";
    private const string PropDetailRowsExpression = "DetailRowsExpression";
    private const string PropFormatStringExpression = "FormatStringExpression";
    private const string PropLineageTag = "LineageTag";
    private const string PropKpi = "KPI";
    private const string PropKpiTargetExpression = PropertyBagKeys.KpiTargetExpression;
    private const string PropKpiStatusExpression = PropertyBagKeys.KpiStatusExpression;
    private const string PropKpiTrendExpression = PropertyBagKeys.KpiTrendExpression;
    private const string PropKpiTargetFormatString = PropertyBagKeys.KpiTargetFormatString;
    private const string PropObjectType = "ObjectType";
    private const string PropUsedInHierarchies = "UsedInHierarchies";
    private const string PropUsedInVariations = "UsedInVariations";
    private const string PropAlternateOf = "AlternateOf";
    private const string PropRowLevelSecurity = "RowLevelSecurity";
    private const string PropPerspectives = "Perspectives";
    private const string PropTableObjectType = "TableObjectType";
    private const string PropRefreshPolicy = "RefreshPolicy";
    private const string PropDataSourceName = "DataSourceName";
    private const string PropDataSourceType = "DataSourceType";
    private const string PropDefaultPowerBIDataSourceVersion = "DefaultPowerBIDataSourceVersion";

    public static ModelSummary Summarize(Database database, string name)
    {
        var model = database.Model;
        return new ModelSummary(
            Name: name,
            CompatibilityLevel: database.CompatibilityLevel,
            Tables: model.Tables.Count,
            Columns: model.Tables.Sum(t => t.Columns.Count),
            Measures: model.Tables.Sum(t => t.Measures.Count),
            Relationships: model.Relationships.Count,
            Roles: model.Roles.Count);
    }

    public static ModelSnapshot Snapshot(Database database, string name)
    {
        var model = database.Model;

        var relIndex = BuildRelationshipIndex(model);
        var rlsIndex = BuildRlsIndex(model);
        var hierarchyUsage = BuildHierarchyUsageIndex(model);
        var perspectiveMembership = BuildPerspectiveMembershipIndex(model);

        var objects = new List<ModelObject>();
        foreach (var table in model.Tables)
            objects.Add(BuildTable(table, relIndex, rlsIndex, hierarchyUsage, perspectiveMembership));

        objects.AddRange(model.Relationships.Select(r => BuildRelationship(r)));
        objects.AddRange(model.Roles.Select(BuildRole));
        objects.AddRange(model.Perspectives.Select(p =>
            Leaf(p.Name, ModelObjectKind.Perspective, $"Perspectives/{Segment(p.Name)}", detail: null,
                description: Desc(p.Description))));
        objects.AddRange(model.Cultures.Select(c =>
            Leaf(c.Name, ModelObjectKind.Culture, $"Cultures/{Segment(c.Name)}", detail: null)));
        objects.AddRange(model.DataSources.Select(BuildDataSource));

        var modelProps = new Dictionary<string, string>
        {
            [PropDefaultPowerBIDataSourceVersion] = model.DefaultPowerBIDataSourceVersion.ToString()
        };
        AddAnnotations(modelProps, model.Annotations);

        return new ModelSnapshot(name, database.CompatibilityLevel, objects, modelProps);
    }

    private static ModelObject BuildTable(
        Table table,
        Dictionary<string, HashSet<RelationshipEntry>> relIndex,
        Dictionary<string, List<string>> rlsIndex,
        Dictionary<string, List<string>> hierarchyUsage,
        Dictionary<string, List<string>> perspectiveMembership)
    {
        var path = Segment(table.Name);
        var children = new List<ModelObject>();
        var isCalcGroup = table.CalculationGroup is not null;

        var tableProps = new Dictionary<string, string>
        {
            [PropDataCategory] = table.DataCategory ?? "",
            [PropLineageTag] = table.LineageTag ?? "",
            [PropTableHasRls] = rlsIndex.ContainsKey(table.Name).ToString().ToLowerInvariant(),
            [PropTableIsCalc] = (table.Partitions.Any(p => p.SourceType == PartitionSourceType.Calculated)).ToString().ToLowerInvariant(),
            [PropTableObjectType] = isCalcGroup ? "CalculationGroup" : "Table",
            [PropRowLevelSecurity] = rlsIndex.TryGetValue(table.Name, out var rls) ? string.Join("\n", rls) : "",
            [PropPerspectives] = perspectiveMembership.TryGetValue(table.Name, out var persp) ? string.Join("\n", persp) : "",
            [PropRefreshPolicy] = TomRefreshPolicyManager.Summarize(table),
            [PropertyBagKeys.DefaultDetailRowsExpression] = table.DefaultDetailRowsDefinition?.Expression ?? "",
            [PropObjectType] = "Table"
        };
        AddAnnotations(tableProps, table.Annotations);

        foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            children.Add(BuildColumn(column, path, relIndex, hierarchyUsage));

        children.AddRange(table.Measures.Select(m => BuildMeasure(m, path)));

        children.AddRange(table.Hierarchies.Select(h => BuildHierarchy(h, path)));

        if (isCalcGroup)
            children.AddRange(table.CalculationGroup!.CalculationItems.Select(ci => BuildCalculationItem(ci, path)));

        children.AddRange(table.Calendars.Select(c => BuildCalendar(c, path)));

        foreach (var partition in table.Partitions)
            children.Add(BuildPartition(partition, path));

        var tableDetail = table.Partitions.Any(p => p.SourceType == PartitionSourceType.Calculated)
            ? "calculated"
            : "regular";

        return new ModelObject(
            table.Name,
            ModelObjectKind.Table,
            path,
            Detail: tableDetail,
            Expression: null,
            Description: Desc(table.Description),
            Hidden: table.IsHidden,
            SourceColumn: null,
            Children: children,
            Properties: tableProps);
    }

    private static ModelObject BuildColumn(
        Column column, string tablePath,
        Dictionary<string, HashSet<RelationshipEntry>> relIndex,
        Dictionary<string, List<string>> hierarchyUsage)
    {
        var colPath = $"{tablePath}/{Segment(column.Name)}";
        var tableName = column.Table.Name;
        var fullyQualified = $"{tableName}[{column.Name}]";

        relIndex.TryGetValue(fullyQualified, out var rels);
        var usedInRels = rels?.Count > 0;
        hierarchyUsage.TryGetValue($"{tableName}|{column.Name}", out var hierarchies);

        var props = new Dictionary<string, string>
        {
            [PropDataType] = column.DataType.ToString(),
            [PropColumnType] = column.Type.ToString(),
            [PropIsKey] = column.IsKey.ToString().ToLowerInvariant(),
            [PropIsAvailableInMdx] = column.IsAvailableInMDX.ToString().ToLowerInvariant(),
            [PropSummarizeBy] = column.SummarizeBy.ToString(),
            [PropFormatString] = column.FormatString ?? "",
            [PropDisplayFolder] = column.DisplayFolder ?? "",
            [PropDataCategory] = column.DataCategory ?? "",
            [PropSortByColumn] = column.SortByColumn?.Name ?? "",
            [PropLineageTag] = column.LineageTag ?? "",
            [PropUsedInRelationships] = usedInRels.ToString().ToLowerInvariant(),
            [PropUsedInHierarchies] = hierarchies is not null ? string.Join("\n", hierarchies) : "",
            [PropUsedInVariations] = string.Join("\n", column.Variations.Select(v => v.Name)),
            [PropAlternateOf] = column.AlternateOf is not null ? "set" : "",
            [PropTableDataCategory] = column.Table.DataCategory ?? "",
            [PropObjectType] = column.Type == ColumnType.Calculated ? "CalculatedColumn" : "DataColumn"
        };
        AddAnnotations(props, column.Annotations);

        if (column is CalculatedTableColumn ctc)
        {
            props[PropColumnType] = "CalculatedTableColumn";
            props[PropObjectType] = "CalculatedTableColumn";
        }

        return new ModelObject(
            column.Name,
            ModelObjectKind.Column,
            colPath,
            Detail: ColumnDetail(column),
            Expression: column.Type == ColumnType.Calculated
                ? ((CalculatedColumn)column).Expression
                : null,
            Description: Desc(column.Description),
            Hidden: column.IsHidden,
            SourceColumn: column is DataColumn dc ? dc.SourceColumn : null,
            Children: [],
            Properties: props);
    }

    private static ModelObject BuildMeasure(Measure measure, string tablePath)
    {
        var path = $"{tablePath}/{Segment(measure.Name)}";
        var props = new Dictionary<string, string>
        {
            [PropDataType] = measure.DataType.ToString(),
            [PropFormatString] = measure.FormatString ?? "",
            [PropDisplayFolder] = measure.DisplayFolder ?? "",
            [PropDetailRowsExpression] = measure.DetailRowsDefinition?.Expression ?? "",
            [PropFormatStringExpression] = measure.FormatStringDefinition?.Expression ?? "",
            [PropKpi] = measure.KPI is null ? "" : "Present",
            [PropKpiTargetExpression] = measure.KPI?.TargetExpression ?? "",
            [PropKpiStatusExpression] = measure.KPI?.StatusExpression ?? "",
            [PropKpiTrendExpression] = measure.KPI?.TrendExpression ?? "",
            [PropLineageTag] = measure.LineageTag ?? "",
            [PropObjectType] = "Measure"
        };
        AddAnnotations(props, measure.Annotations);

        return new ModelObject(
            measure.Name,
            ModelObjectKind.Measure,
            path,
            Detail: null,
            Expression: measure.Expression,
            Description: Desc(measure.Description),
            Hidden: measure.IsHidden,
            SourceColumn: null,
            Children: measure.KPI is null ? [] : [BuildKpi(measure.KPI, path)],
            Properties: props);
    }

    private static ModelObject BuildKpi(KPI kpi, string measurePath)
        => new(
            "KPI",
            ModelObjectKind.Kpi,
            $"{measurePath}/KPI",
            Detail: null,
            Expression: kpi.StatusExpression,
            Description: Desc(kpi.Description),
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string>
            {
                [PropKpiTargetExpression] = kpi.TargetExpression ?? "",
                [PropKpiStatusExpression] = kpi.StatusExpression ?? "",
                [PropKpiTrendExpression] = kpi.TrendExpression ?? "",
                [PropKpiTargetFormatString] = kpi.TargetFormatString ?? "",
                [PropObjectType] = "KPI"
            });

    private static ModelObject BuildHierarchy(Hierarchy hierarchy, string tablePath)
    {
        var path = $"{tablePath}/{Segment(hierarchy.Name)}";
        var levels = hierarchy.Levels
            .OrderBy(l => l.Ordinal)
            .Select(l => Leaf(
                l.Name,
                ModelObjectKind.Level,
                $"{path}/{Segment(l.Name)}",
                detail: l.Column?.Name))
            .ToList();

        return new ModelObject(
            hierarchy.Name,
            ModelObjectKind.Hierarchy,
            path,
            Detail: $"{levels.Count} levels",
            Expression: null,
            Description: Desc(hierarchy.Description),
            Hidden: hierarchy.IsHidden,
            SourceColumn: null,
            Children: levels);
    }

    private static ModelObject BuildPartition(Partition partition, string tablePath)
    {
        var (dataSourceName, dataSourceType) = PartitionDataSource(partition);
        var props = new Dictionary<string, string>
        {
            [PropPartitionSourceType] = partition.SourceType.ToString(),
            [PropPartitionMode] = partition.Mode.ToString(),
            [PropPartitionDataView] = partition.DataView.ToString(),
            [PropPartitionQueryGroup] = partition.QueryGroup?.Name ?? "",
            [PropDataSourceName] = dataSourceName,
            [PropDataSourceType] = dataSourceType,
            [PropObjectType] = "Partition"
        };

        return new ModelObject(
            partition.Name,
            ModelObjectKind.Partition,
            $"{tablePath}/{Segment(partition.Name)}",
            Detail: PartitionDetail(partition),
            Expression: PartitionExpression(partition),
            Description: Desc(partition.Description),
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: props);
    }

    private static ModelObject BuildRelationship(Relationship relationship)
    {
        var path = $"Relationships/{Segment(relationship.Name)}";

        if (relationship is not SingleColumnRelationship single)
            return Leaf(relationship.Name, ModelObjectKind.Relationship, path, detail: null);

        var name = $"{single.FromColumn.Table.Name}[{single.FromColumn.Name}] -> " +
                   $"{single.ToColumn.Table.Name}[{single.ToColumn.Name}]";
        var detail = $"{name} ({Cardinality(single)}, {(single.IsActive ? "active" : "inactive")})";

        var props = new Dictionary<string, string>
        {
            [PropFromColumn] = single.FromColumn.Table.Name + "[" + single.FromColumn.Name + "]",
            [PropToColumn] = single.ToColumn.Table.Name + "[" + single.ToColumn.Name + "]",
            [PropFromTable] = single.FromColumn.Table.Name,
            [PropToTable] = single.ToColumn.Table.Name,
            [PropFromCardinality] = single.FromCardinality.ToString(),
            [PropToCardinality] = single.ToCardinality.ToString(),
            [PropCrossFilteringBehavior] = single.CrossFilteringBehavior.ToString(),
            [PropIsActive] = single.IsActive.ToString().ToLowerInvariant(),
            [PropObjectType] = "Relationship"
        };
        AddAnnotations(props, single.Annotations);

        return new ModelObject(
            name,
            ModelObjectKind.Relationship,
            path,
            Detail: detail,
            Expression: null,
            Description: null,
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: props);
    }

    private static ModelObject BuildRole(ModelRole role)
    {
        var path = $"Roles/{Segment(role.Name)}";
        var children = role.Members
            .Select(m => Leaf(
                m.MemberName,
                ModelObjectKind.RoleMember,
                $"{path}/{Segment(m.MemberName)}",
                detail: null))
            .ToList();

        children.AddRange(role.TablePermissions.Select(tp => BuildTablePermission(tp, path)));

        var rlsExpressions = new List<string>();
        foreach (var tp in role.TablePermissions)
        {
            if (!string.IsNullOrWhiteSpace(tp.FilterExpression))
                rlsExpressions.Add($"{tp.Table.Name}: {tp.FilterExpression}");
        }

        var props = new Dictionary<string, string>
        {
            [PropRlsExpression] = string.Join("\n", rlsExpressions),
            [PropObjectType] = "ModelRole"
        };
        AddAnnotations(props, role.Annotations);

        return new ModelObject(
            role.Name,
            ModelObjectKind.Role,
            path,
            Detail: role.ModelPermission.ToString(),
            Expression: null,
            Description: Desc(role.Description),
            Hidden: false,
            SourceColumn: null,
            Children: children,
            Properties: props);
    }

    private static ModelObject BuildTablePermission(TablePermission permission, string rolePath)
        => new(
            permission.Name,
            ModelObjectKind.TablePermission,
            $"{rolePath}/{Segment(permission.Name)}",
            Detail: permission.MetadataPermission.ToString().ToLowerInvariant(),
            Expression: string.IsNullOrWhiteSpace(permission.FilterExpression) ? null : permission.FilterExpression,
            Description: null,
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { [PropObjectType] = "TablePermission" });

    private static ModelObject BuildCalculationItem(CalculationItem item, string tablePath)
        => new(
            item.Name,
            ModelObjectKind.CalculationItem,
            $"{tablePath}/{Segment(item.Name)}",
            Detail: null,
            Expression: item.Expression,
            Description: Desc(item.Description),
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { [PropObjectType] = "CalculationItem" });

    private static ModelObject BuildCalendar(Calendar calendar, string tablePath)
        => new(
            calendar.Name,
            ModelObjectKind.Calendar,
            $"{tablePath}/{Segment(calendar.Name)}",
            Detail: null,
            Expression: null,
            Description: Desc(calendar.Description),
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { [PropObjectType] = "Calendar" });

    private static ModelObject BuildDataSource(DataSource dataSource)
        => new(
            dataSource.Name,
            ModelObjectKind.DataSource,
            $"DataSources/{Segment(dataSource.Name)}",
            Detail: null,
            Expression: null,
            Description: Desc(dataSource.Description),
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string>
            {
                [PropDataSourceType] = dataSource is StructuredDataSource ? "Structured" : "Provider",
                [PropObjectType] = "DataSource"
            });

    private static (string Name, string Type) PartitionDataSource(Partition partition)
        => partition.Source is QueryPartitionSource { DataSource: { } ds }
            ? (ds.Name, ds is StructuredDataSource ? "Structured" : "Provider")
            : ("", "");

    private static void AddAnnotations(Dictionary<string, string> props, IEnumerable<Annotation> annotations)
    {
        foreach (var annotation in annotations)
            props[$"Annotation:{annotation.Name}"] = annotation.Value ?? "";
    }

    private static Dictionary<string, List<string>> BuildHierarchyUsageIndex(Model model)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in model.Tables)
            foreach (var hierarchy in table.Hierarchies)
                foreach (var level in hierarchy.Levels)
                {
                    if (level.Column is null) continue;
                    var key = $"{table.Name}|{level.Column.Name}";
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = [];
                    list.Add(hierarchy.Name);
                }

        return index;
    }

    private static Dictionary<string, List<string>> BuildPerspectiveMembershipIndex(Model model)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var perspective in model.Perspectives)
            foreach (PerspectiveTable perspectiveTable in perspective.PerspectiveTables)
            {
                if (!index.TryGetValue(perspectiveTable.Name, out var list))
                    index[perspectiveTable.Name] = list = [];
                list.Add(perspective.Name);
            }

        return index;
    }

    private static Dictionary<string, HashSet<RelationshipEntry>> BuildRelationshipIndex(Model model)
    {
        var index = new Dictionary<string, HashSet<RelationshipEntry>>();

        foreach (var rel in model.Relationships)
        {
            if (rel is not SingleColumnRelationship single) continue;

            var fromKey = $"{single.FromColumn.Table.Name}[{single.FromColumn.Name}]";
            var toKey = $"{single.ToColumn.Table.Name}[{single.ToColumn.Name}]";
            var entry = new RelationshipEntry(
                single.FromColumn.Table.Name, single.FromColumn.Name,
                single.ToColumn.Table.Name, single.ToColumn.Name,
                single.FromCardinality.ToString(), single.ToCardinality.ToString(),
                single.CrossFilteringBehavior.ToString(), single.IsActive);

            AddToIndex(index, fromKey, entry);
            AddToIndex(index, toKey, entry);
        }

        return index;
    }

    private static void AddToIndex(Dictionary<string, HashSet<RelationshipEntry>> index, string key, RelationshipEntry entry)
    {
        if (!index.TryGetValue(key, out var set))
        {
            set = [];
            index[key] = set;
        }
        set.Add(entry);
    }

    private static Dictionary<string, List<string>> BuildRlsIndex(Model model)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in model.Roles)
        {
            foreach (var tp in role.TablePermissions)
            {
                if (string.IsNullOrWhiteSpace(tp.FilterExpression)) continue;
                if (!index.TryGetValue(tp.Table.Name, out var list))
                {
                    list = [];
                    index[tp.Table.Name] = list;
                }
                list.Add(tp.FilterExpression);
            }
        }

        return index;
    }

    private static string ColumnDetail(Column column)
    {
        var type = column.DataType.ToString().ToLowerInvariant();
        return column.Type == ColumnType.Calculated ? $"{type}, calculated" : type;
    }

    private static string PartitionDetail(Partition partition)
        => partition.SourceType == PartitionSourceType.Calculated
            ? "calculated"
            : partition.Mode.ToString().ToLowerInvariant();

    private static string? PartitionExpression(Partition partition) => partition.Source switch
    {
        MPartitionSource m => m.Expression,
        CalculatedPartitionSource c => c.Expression,
        _ => null
    };

    private static string Cardinality(SingleColumnRelationship r) =>
        (r.FromCardinality, r.ToCardinality) switch
        {
            (RelationshipEndCardinality.Many, RelationshipEndCardinality.One) => "many-to-one",
            (RelationshipEndCardinality.One, RelationshipEndCardinality.Many) => "one-to-many",
            (RelationshipEndCardinality.One, RelationshipEndCardinality.One) => "one-to-one",
            (RelationshipEndCardinality.Many, RelationshipEndCardinality.Many) => "many-to-many",
            _ => $"{r.FromCardinality}-to-{r.ToCardinality}"
        };

    private static ModelObject Leaf(
        string name, ModelObjectKind kind, string path, string? detail,
        string? description = null, bool hidden = false)
        => new(name, kind, path, detail, Expression: null, description, hidden, SourceColumn: null, Children: []);

    private static string? Desc(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static string Segment(string name)
        => name.Contains('/') ? $"'{name}'" : name;

    private sealed record RelationshipEntry(
        string FromTable, string FromColumn,
        string ToTable, string ToColumn,
        string FromCardinality, string ToCardinality,
        string CrossFilteringBehavior, bool IsActive);
}
