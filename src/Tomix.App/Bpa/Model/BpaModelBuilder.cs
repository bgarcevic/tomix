using Tomix.App.Dax;
using Tomix.Core.Models;

namespace Tomix.App.Bpa.Model;

/// <summary>
/// Builds the <see cref="BpaModel"/> adapter graph from a provider-agnostic
/// <see cref="ModelSnapshot"/>: maps snapshot properties onto typed members and derives the
/// navigations the rule expressions need (dependency graph, relationship participation,
/// reverse references, sort-by/hierarchy usage, perspective membership, calculation items, …).
/// </summary>
public static class BpaModelBuilder
{
    public static BpaModel Build(ModelSnapshot snapshot)
    {
        var model = new BpaModel
        {
            Name = snapshot.Name,
            Source = SyntheticModelObject(snapshot.Properties),
            DefaultPowerBIDataSourceVersion =
                snapshot.Properties is not null && snapshot.Properties.TryGetValue("DefaultPowerBIDataSourceVersion", out var v) ? v : "",
        };

        var allObjects = Flatten(snapshot.Objects).ToList();
        var tableObjects = snapshot.Objects.Where(o => o.Kind == ModelObjectKind.Table).ToList();
        var perspectiveNames = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.Perspective)
            .Select(o => o.Name)
            .ToList();

        var tables = new List<BpaTable>();
        var columns = new List<BpaColumn>();
        var measures = new List<BpaMeasure>();
        var partitions = new List<BpaPartition>();
        var calculationItems = new List<BpaCalculationItem>();
        var hierarchies = new List<BpaHierarchy>();

        foreach (var tableObj in tableObjects)
        {
            var table = new BpaTable
            {
                Source = tableObj,
                Model = model,
                Name = tableObj.Name,
                Description = tableObj.Description ?? "",
                IsHidden = tableObj.Hidden,
                DataCategory = tableObj.Property("DataCategory") ?? "",
                RowLevelSecurity = SplitLines(tableObj.Property("RowLevelSecurity")),
            };

            var tableColumns = new List<BpaColumn>();
            var tablePartitions = new List<BpaPartition>();
            var tableCalcItems = new List<BpaCalculationItem>();

            foreach (var child in tableObj.Children)
            {
                switch (child.Kind)
                {
                    case ModelObjectKind.Column:
                        var column = BuildColumn(child, table, model);
                        tableColumns.Add(column);
                        columns.Add(column);
                        break;
                    case ModelObjectKind.Measure:
                        measures.Add(BuildMeasure(child, table, model));
                        break;
                    case ModelObjectKind.Partition:
                        var partition = BuildPartition(child, model);
                        tablePartitions.Add(partition);
                        partitions.Add(partition);
                        break;
                    case ModelObjectKind.CalculationItem:
                        var item = BuildCalculationItem(child, model);
                        tableCalcItems.Add(item);
                        calculationItems.Add(item);
                        break;
                    case ModelObjectKind.Hierarchy:
                        hierarchies.Add(BuildHierarchy(child, model));
                        break;
                }
            }

            table.Columns = tableColumns;
            table.Partitions = tablePartitions;
            table.CalculationItems = tableCalcItems;
            table.ObjectTypeName = TableObjectTypeName(tableObj, tablePartitions);

            var memberPerspectives = new HashSet<string>(SplitLines(tableObj.Property("Perspectives")), StringComparer.OrdinalIgnoreCase);
            table.InPerspective = perspectiveNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n, memberPerspectives.Contains, StringComparer.OrdinalIgnoreCase);

            tables.Add(table);
        }

        var tablesByName = new Dictionary<string, BpaTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
            tablesByName[t.Name] = t;
        var columnsByKey = new Dictionary<string, BpaColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
            columnsByKey[$"{c.Table.Name}|{c.Name}"] = c;

        var relationships = allObjects
            .Where(o => o.Kind == ModelObjectKind.Relationship)
            .Select(r => BuildRelationship(r, model, tablesByName, columnsByKey))
            .ToList();

        var roles = allObjects
            .Where(o => o.Kind == ModelObjectKind.Role)
            .Select(r => BuildRole(r, model))
            .ToList();

        var perspectives = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.Perspective)
            .Select(p => new BpaPerspective { Source = p, Model = model, Name = p.Name, Description = p.Description ?? "" })
            .ToList();

        var dataSources = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.DataSource)
            .Select(d => new BpaDataSource
            {
                Source = d,
                Model = model,
                Name = d.Name,
                Description = d.Description ?? "",
                Type = d.Property("DataSourceType") ?? "",
            })
            .ToList();

        // Dependency graphs for DAX-bearing objects.
        var measureNames = new HashSet<string>(measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var columnNames = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var measure in measures)
            measure.DependsOn = ComputeDependsOn(measure.Expression, measureNames, columnNames);
        foreach (var column in columns)
            column.DependsOn = ComputeDependsOn(column.Source.Expression, measureNames, columnNames);
        foreach (var item in calculationItems)
            item.DependsOn = ComputeDependsOn(item.Expression, measureNames, columnNames);
        foreach (var table in tables)
            table.DependsOn = ComputeDependsOn(CalculatedTableExpression(table), measureNames, columnNames);

        AssignSortByUsage(tables);
        AssignRelationshipParticipation(tables, columns, relationships);
        AssignReferencedBy(measures, columns);
        AssignDataSourceUsage(dataSources, partitions);

        model.Tables = tables;
        model.AllMeasures = measures;
        model.AllColumns = columns;
        model.AllPartitions = partitions;
        model.Roles = roles;
        model.Relationships = relationships;
        model.AllCalculationItems = calculationItems;
        model.DataSources = dataSources;
        model.Perspectives = perspectives;
        model.Hierarchies = hierarchies;

        return model;
    }

    private static BpaColumn BuildColumn(ModelObject obj, BpaTable table, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Table = table,
            Name = obj.Name,
            Description = obj.Description ?? "",
            IsHidden = obj.Hidden,
            DataType = obj.Property("DataType") ?? "",
            IsKey = IsTrue(obj.Property("IsKey")),
            IsAvailableInMDX = IsTrue(obj.Property("IsAvailableInMdx")),
            FormatString = obj.Property("FormatString") ?? "",
            DataCategory = obj.Property("DataCategory") ?? "",
            SummarizeBy = obj.Property("SummarizeBy") ?? "",
            SourceColumn = obj.SourceColumn ?? obj.Property("SourceColumn") ?? "",
            SortByColumn = string.IsNullOrEmpty(obj.Property("SortByColumn")) ? null : obj.Property("SortByColumn"),
            ColumnType = obj.Property("ColumnType") ?? "Data",
            UsedInHierarchies = SplitLines(obj.Property("UsedInHierarchies")),
            UsedInVariations = SplitLines(obj.Property("UsedInVariations")),
            AlternateOf = string.IsNullOrEmpty(obj.Property("AlternateOf")) ? null : obj.Property("AlternateOf"),
        };

    private static BpaMeasure BuildMeasure(ModelObject obj, BpaTable table, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Table = table,
            Name = obj.Name,
            Description = obj.Description ?? "",
            IsHidden = obj.Hidden,
            DataType = obj.Property("DataType") ?? "",
            FormatString = obj.Property("FormatString") ?? "",
            FormatStringExpression = obj.Property("FormatStringExpression") ?? "",
            Expression = obj.Expression ?? "",
            DaxObjectName = $"[{obj.Name}]",
        };

    private static BpaPartition BuildPartition(ModelObject obj, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Name = obj.Name,
            Description = obj.Description ?? "",
            IsHidden = obj.Hidden,
            SourceType = obj.Property("PartitionSourceType") ?? "",
            Mode = obj.Property("PartitionMode") ?? "",
            Query = obj.Expression ?? "",
            DataSource = new BpaDataSourceRef
            {
                Name = obj.Property("DataSourceName") ?? "",
                Type = obj.Property("DataSourceType") ?? "",
            },
        };

    private static BpaCalculationItem BuildCalculationItem(ModelObject obj, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Name = obj.Name,
            Description = obj.Description ?? "",
            IsHidden = obj.Hidden,
            Expression = obj.Expression ?? "",
        };

    private static BpaHierarchy BuildHierarchy(ModelObject obj, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Name = obj.Name,
            Description = obj.Description ?? "",
            IsHidden = obj.Hidden,
        };

    private static BpaRelationship BuildRelationship(
        ModelObject obj,
        BpaModel model,
        Dictionary<string, BpaTable> tablesByName,
        Dictionary<string, BpaColumn> columnsByKey)
    {
        var fromTableName = obj.Property("FromTable") ?? "";
        var toTableName = obj.Property("ToTable") ?? "";
        var fromColumnName = ColumnNameOf(obj.Property("FromColumn"));
        var toColumnName = ColumnNameOf(obj.Property("ToColumn"));

        return new BpaRelationship
        {
            Source = obj,
            Model = model,
            Name = obj.Name,
            Description = obj.Description ?? "",
            FromTable = tablesByName.GetValueOrDefault(fromTableName),
            ToTable = tablesByName.GetValueOrDefault(toTableName),
            FromColumn = columnsByKey.GetValueOrDefault($"{fromTableName}|{fromColumnName}"),
            ToColumn = columnsByKey.GetValueOrDefault($"{toTableName}|{toColumnName}"),
            FromCardinality = obj.Property("FromCardinality") ?? "",
            ToCardinality = obj.Property("ToCardinality") ?? "",
            CrossFilteringBehavior = obj.Property("CrossFilteringBehavior") ?? "",
            IsActive = IsTrue(obj.Property("IsActive")),
        };
    }

    private static BpaRole BuildRole(ModelObject obj, BpaModel model)
        => new()
        {
            Source = obj,
            Model = model,
            Name = obj.Name,
            Description = obj.Description ?? "",
            Members = obj.Children.Where(c => c.Kind == ModelObjectKind.RoleMember).Select(c => c.Name).ToList(),
            RowLevelSecurity = (obj.Property("RlsExpression") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        };

    /// <summary>
    /// Groups the references in a DAX expression by referenced object type, tagging each with
    /// whether it was written fully-qualified. A qualified reference is resolved to a column
    /// first (a table qualifier implies a column); a lone reference to a measure first.
    /// </summary>
    private static IReadOnlyList<BpaDependsOnEntry> ComputeDependsOn(
        string? expression,
        HashSet<string> measureNames,
        HashSet<string> columnNames)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var byType = new Dictionary<string, List<BpaReference>>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in DaxReferenceExtractor.Extract(expression))
        {
            // Table-only shapes carry no bracketed name; BPA's DependsOn tracks columns/measures.
            if (reference.Object is not { } name)
                continue;

            string? objectType = reference.FullyQualified
                ? (columnNames.Contains(name) ? "Column"
                    : measureNames.Contains(name) ? "Measure" : null)
                : (measureNames.Contains(name) ? "Measure"
                    : columnNames.Contains(name) ? "Column" : null);

            if (objectType is null)
                continue;

            if (!byType.TryGetValue(objectType, out var list))
                byType[objectType] = list = [];

            list.Add(new BpaReference
            {
                FullyQualified = reference.FullyQualified,
                ObjectType = objectType,
                Name = reference.Object,
                Table = reference.Table,
            });
        }

        return byType
            .Select(kvp => new BpaDependsOnEntry
            {
                Key = new BpaDependsOnKey { ObjectType = kvp.Key },
                Value = kvp.Value,
            })
            .ToList();
    }

    /// <summary>Populates <see cref="BpaColumn.UsedInSortBy"/> — columns that sort by each column.</summary>
    private static void AssignSortByUsage(List<BpaTable> tables)
    {
        foreach (var table in tables)
        {
            foreach (var column in table.Columns)
            {
                column.UsedInSortBy = table.Columns
                    .Where(c => c.SortByColumn is not null && Eq(c.SortByColumn, column.Name))
                    .ToList();
            }
        }
    }

    private static void AssignRelationshipParticipation(
        List<BpaTable> tables,
        List<BpaColumn> columns,
        List<BpaRelationship> relationships)
    {
        foreach (var table in tables)
            table.UsedInRelationships = relationships
                .Where(r => Eq(r.FromTable?.Name, table.Name) || Eq(r.ToTable?.Name, table.Name))
                .ToList();

        foreach (var column in columns)
        {
            var tableName = column.Table.Name;
            column.UsedInRelationships = relationships
                .Where(r =>
                    (Eq(r.FromTable?.Name, tableName) && Eq(r.FromColumn?.Name, column.Name)) ||
                    (Eq(r.ToTable?.Name, tableName) && Eq(r.ToColumn?.Name, column.Name)))
                .ToList();
        }
    }

    private static void AssignReferencedBy(List<BpaMeasure> measures, List<BpaColumn> columns)
    {
        var measuresByRef = new Dictionary<string, List<BpaMeasure>>(StringComparer.OrdinalIgnoreCase);
        var countByRef = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Record(IEnumerable<BpaDependsOnEntry> dependsOn, BpaMeasure? referencingMeasure)
        {
            // Key references by qualified identity so columns that share a name across tables don't
            // collapse: a qualified 'Table'[Col] counts only for that table's column. Dedup per
            // source object by composite key.
            var keys = new HashSet<string>(
                dependsOn.SelectMany(e => e.Value).Select(ReferenceKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                countByRef[key] = countByRef.GetValueOrDefault(key) + 1;
                if (referencingMeasure is not null)
                {
                    if (!measuresByRef.TryGetValue(key, out var list))
                        measuresByRef[key] = list = [];
                    list.Add(referencingMeasure);
                }
            }
        }

        foreach (var measure in measures)
            Record(measure.DependsOn, measure);
        foreach (var column in columns)
            Record(column.DependsOn, null);

        foreach (var measure in measures)
            measure.ReferencedBy = MakeReferencedBy([MeasureKey(measure.Name)], measuresByRef, countByRef);
        foreach (var column in columns)
            column.ReferencedBy = MakeReferencedBy(
                // A column is referenced by qualified refs to its exact table[name], plus any
                // unqualified [name] refs (which DAX resolves by context and we cannot disambiguate).
                [ColumnKey(column.Table.Name, column.Name), ColumnWildcardKey(column.Name)],
                measuresByRef,
                countByRef);
    }

    private static string MeasureKey(string name) => "M " + name;
    private static string ColumnKey(string table, string name) => "C " + table + " " + name;
    private static string ColumnWildcardKey(string name) => "C * " + name;

    private static string ReferenceKey(BpaReference reference)
        => reference.ObjectType.Equals("Measure", StringComparison.OrdinalIgnoreCase)
            ? MeasureKey(reference.Name)
            : reference.FullyQualified && !string.IsNullOrEmpty(reference.Table)
                ? ColumnKey(reference.Table!, reference.Name)
                : ColumnWildcardKey(reference.Name);

    private static void AssignDataSourceUsage(List<BpaDataSource> dataSources, List<BpaPartition> partitions)
    {
        foreach (var ds in dataSources)
            ds.UsedByPartitions = partitions
                .Where(p => Eq(p.DataSource.Name, ds.Name))
                .ToList();
    }

    private static BpaReferencedBy MakeReferencedBy(
        IReadOnlyList<string> keys,
        Dictionary<string, List<BpaMeasure>> measuresByRef,
        Dictionary<string, int> countByRef)
    {
        var count = 0;
        List<BpaMeasure>? referencingMeasures = null;

        foreach (var key in keys)
        {
            count += countByRef.GetValueOrDefault(key);
            if (measuresByRef.TryGetValue(key, out var list))
                (referencingMeasures ??= []).AddRange(list);
        }

        return new BpaReferencedBy
        {
            AllMeasures = referencingMeasures is null ? [] : referencingMeasures.Distinct().ToList(),
            Count = count,
        };
    }

    /// <summary>The DAX of a calculated table (its calculated partition's query), or empty.</summary>
    private static string CalculatedTableExpression(BpaTable table)
        => table.Partitions
            .FirstOrDefault(p => p.SourceType.Equals("Calculated", StringComparison.OrdinalIgnoreCase))?.Query
            ?? "";

    private static string TableObjectTypeName(ModelObject table, List<BpaPartition> partitions)
    {
        if (IsTrue(table.Property("TableIsCalc")))
            return "Calculated Table";
        if (partitions.Any(p => p.Mode.Equals("DirectQuery", StringComparison.OrdinalIgnoreCase)))
            return "Table (DirectQuery)";
        return "Table";
    }

    /// <summary>Extracts the column name from a "Table[Column]" / "'Table'[Column]" reference.</summary>
    private static string ColumnNameOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return "";
        var open = reference.IndexOf('[');
        var close = reference.LastIndexOf(']');
        return open >= 0 && close > open
            ? reference[(open + 1)..close]
            : reference;
    }

    private static IReadOnlyList<string> SplitLines(string? value)
        => string.IsNullOrEmpty(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static ModelObject SyntheticModelObject(IReadOnlyDictionary<string, string>? modelProperties)
    {
        // Carry model-level properties (including "Annotation:*" entries, e.g. the BPA ignore list)
        // onto the synthetic object so model-scoped rules and the ignore store can read them.
        var properties = new Dictionary<string, string> { ["ObjectType"] = "Model" };
        if (modelProperties is not null)
            foreach (var (key, value) in modelProperties)
                properties[key] = value;

        return new ModelObject(
            "Model",
            ModelObjectKind.Table,
            "Model",
            Detail: null,
            Expression: null,
            Description: null,
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: properties);
    }

    private static bool IsTrue(string? value)
        => value is not null && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool Eq(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            yield return obj;
            foreach (var child in Flatten(obj.Children))
                yield return child;
        }
    }
}
