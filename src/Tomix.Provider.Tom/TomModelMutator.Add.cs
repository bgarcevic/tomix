using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Object-creation logic for <see cref="TomModelMutator"/>. Every advertised <c>tx add --type</c>
/// value maps to a builder here that creates the corresponding TOM object under the requested path
/// and reports <c>Changed=true</c>. Builders honor <c>--if-not-exists</c> and the partition/data
/// source options (mode, expression, columns, connection details).
/// </summary>
public sealed partial class TomModelMutator
{
    private enum PartitionKind
    {
        M,
        Entity,
        PolicyRange
    }

    private ModelObjectMutationResult AddCalcTable(string path, ModelObjectAddRequest request)
    {
        if (path.Contains('/'))
            throw new InvalidOperationException($"Cannot add a CalcTable at path '{request.Path}'. Check that -t matches the path shape.");

        var existing = FindTable(path);
        if (existing is not null)
            return ExistingOrThrow($"{existing.Name}", request.IfNotExists, path);

        var table = new Table { Name = path };
        table.Partitions.Add(new Partition
        {
            Name = path,
            Mode = ParseMode(request.Mode),
            Source = new CalculatedPartitionSource
            {
                Expression = request.PartitionExpression ?? request.Value ?? "{1}"
            }
        });
        _database.Model.Tables.Add(table);

        ApplyProperties(table, request.Properties);
        return new ModelObjectMutationResult(table.Name, Changed: true);
    }

    private ModelObjectMutationResult AddCalcGroup(string path, ModelObjectAddRequest request)
    {
        if (path.Contains('/'))
            throw new InvalidOperationException($"Cannot add a CalcGroup at path '{request.Path}'. Check that -t matches the path shape.");

        var existing = FindTable(path);
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var table = new Table { Name = path };
        table.CalculationGroup = new CalculationGroup();
        table.Columns.Add(new DataColumn
        {
            Name = "Name",
            DataType = DataType.String,
            SourceColumn = "Name"
        });
        _database.Model.Tables.Add(table);

        ApplyProperties(table, request.Properties);
        return new ModelObjectMutationResult(table.Name, Changed: true);
    }

    private ModelObjectMutationResult AddCalcColumn(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "CalcColumn");

        var existing = table.Columns.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var column = new CalculatedColumn
        {
            Name = name,
            Expression = request.Value ?? ""
        };
        table.Columns.Add(column);

        ApplyProperties(column, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{column.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddDataColumn(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "DataColumn");

        var existing = table.Columns.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var column = new DataColumn
        {
            Name = name,
            DataType = DataType.String,
            SourceColumn = request.Value ?? name
        };
        table.Columns.Add(column);

        ApplyProperties(column, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{column.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddHierarchy(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "Hierarchy");

        var existing = table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var hierarchy = new Hierarchy { Name = name };
        // A hierarchy needs at least one level to serialize; seed it from the first real column.
        var seed = table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber);
        if (seed is not null)
            hierarchy.Levels.Add(new Level { Name = seed.Name, Ordinal = 0, Column = seed });
        table.Hierarchies.Add(hierarchy);

        ApplyProperties(hierarchy, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{hierarchy.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddLevel(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 3)
            throw new InvalidOperationException($"Cannot add a Level at path '{request.Path}'. Expected 'Table/Hierarchy/Level'.");

        var table = FindTable(parts[0]) ??
                    throw new InvalidOperationException($"Table not found: {parts[0]}");
        var hierarchy = table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, parts[1])) ??
                        throw new InvalidOperationException($"Hierarchy not found: {parts[1]}");

        var existing = hierarchy.Levels.FirstOrDefault(l => NameEquals(l.Name, parts[2]));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{hierarchy.Name}/{existing.Name}", request.IfNotExists, path);

        var column = request.Value is { Length: > 0 }
            ? table.Columns.FirstOrDefault(c => NameEquals(c.Name, request.Value))
              ?? throw new InvalidOperationException($"Column not found: {request.Value}")
            : table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber)
              ?? throw new InvalidOperationException($"Table '{table.Name}' has no column to bind the level to.");

        var level = new Level
        {
            Name = parts[2],
            Ordinal = hierarchy.Levels.Count,
            Column = column
        };
        hierarchy.Levels.Add(level);

        ApplyProperties(level, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{hierarchy.Name}/{level.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddCalendar(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "Calendar");

        var existing = table.Calendars.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var calendar = new Calendar { Name = name };
        table.Calendars.Add(calendar);

        ApplyProperties(calendar, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{calendar.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddCalcItem(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
            throw new InvalidOperationException($"Cannot add a CalcItem at path '{request.Path}'. Expected 'CalcGroup/Item'.");

        var table = FindTable(parts[0]) ??
                    throw new InvalidOperationException($"Table not found: {parts[0]}");
        if (table.CalculationGroup is null)
            throw new InvalidOperationException($"Table '{table.Name}' is not a calculation group.");

        var existing = table.CalculationGroup.CalculationItems.FirstOrDefault(i => NameEquals(i.Name, parts[1]));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var item = new CalculationItem
        {
            Name = parts[1],
            Expression = request.Value ?? "SELECTEDMEASURE()",
            Ordinal = table.CalculationGroup.CalculationItems.Count
        };
        table.CalculationGroup.CalculationItems.Add(item);

        ApplyProperties(item, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{item.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddKpi(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
            throw new InvalidOperationException($"Cannot add a KPI at path '{request.Path}'. Expected 'Table/Measure'.");

        var table = FindTable(parts[0]) ??
                    throw new InvalidOperationException($"Table not found: {parts[0]}");
        var measure = table.Measures.FirstOrDefault(m => NameEquals(m.Name, parts[1])) ??
                      throw new InvalidOperationException($"Measure not found: {parts[1]}");

        if (measure.KPI is not null)
            return ExistingOrThrow($"{table.Name}/{measure.Name}", request.IfNotExists, path);

        measure.KPI = new KPI
        {
            TargetExpression = request.Value ?? "0",
            StatusExpression = "0"
        };

        ApplyProperties(measure.KPI, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{measure.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddPartition(string path, ModelObjectAddRequest request, PartitionKind kind)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "Partition");

        var existing = table.Partitions.FirstOrDefault(p => NameEquals(p.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);

        var partition = new Partition
        {
            Name = name,
            Mode = ParseMode(request.Mode),
            Source = BuildPartitionSource(kind, name, request)
        };
        table.Partitions.Add(partition);

        ApplyProperties(partition, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{partition.Name}", Changed: true);
    }

    private static PartitionSource BuildPartitionSource(PartitionKind kind, string name, ModelObjectAddRequest request)
        => kind switch
        {
            PartitionKind.Entity => new EntityPartitionSource
            {
                EntityName = request.SourceTable ?? request.Value ?? name,
                SchemaName = string.IsNullOrWhiteSpace(request.SourceDatabase) ? null : request.SourceDatabase
            },
            PartitionKind.PolicyRange => new PolicyRangePartitionSource
            {
                Start = new DateTime(2020, 1, 1),
                End = new DateTime(2021, 1, 1),
                Granularity = RefreshGranularityType.Day
            },
            _ => new MPartitionSource
            {
                Expression = request.PartitionExpression ?? request.Value ?? "let Source = #table({}, {}) in Source"
            }
        };

    private ModelObjectMutationResult AddExpression(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "Expression");

        var existing = _database.Model.Expressions.FirstOrDefault(e => NameEquals(e.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var expression = new NamedExpression
        {
            Name = name,
            Kind = ExpressionKind.M,
            Expression = request.Value ?? "1 meta [IsParameterQuery=false]"
        };
        _database.Model.Expressions.Add(expression);

        ApplyProperties(expression, request.Properties);
        return new ModelObjectMutationResult(expression.Name, Changed: true);
    }

    private ModelObjectMutationResult AddFunction(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "Function");

        var existing = _database.Model.Functions.FirstOrDefault(f => NameEquals(f.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var function = new Function
        {
            Name = name,
            Expression = request.Value ?? "() => 1"
        };
        _database.Model.Functions.Add(function);

        ApplyProperties(function, request.Properties);
        return new ModelObjectMutationResult(function.Name, Changed: true);
    }

    private ModelObjectMutationResult AddPerspective(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "Perspective");

        var existing = _database.Model.Perspectives.FirstOrDefault(p => NameEquals(p.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var perspective = new Perspective { Name = name };
        _database.Model.Perspectives.Add(perspective);

        ApplyProperties(perspective, request.Properties);
        return new ModelObjectMutationResult(perspective.Name, Changed: true);
    }

    private ModelObjectMutationResult AddCulture(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "Culture");

        var existing = _database.Model.Cultures.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var culture = new Culture { Name = name };
        _database.Model.Cultures.Add(culture);

        ApplyProperties(culture, request.Properties);
        return new ModelObjectMutationResult(culture.Name, Changed: true);
    }

    private ModelObjectMutationResult AddProviderDataSource(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "ProviderDataSource");

        var existing = _database.Model.DataSources.FirstOrDefault(d => NameEquals(d.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var dataSource = new ProviderDataSource
        {
            Name = name,
            ConnectionString = request.ConnectionString
                ?? BuildConnectionString(request.Endpoint, request.SourceDatabase)
        };
        if (!string.IsNullOrWhiteSpace(request.Source))
            dataSource.Provider = request.Source;

        _database.Model.DataSources.Add(dataSource);

        ApplyProperties(dataSource, request.Properties);
        return new ModelObjectMutationResult(dataSource.Name, Changed: true);
    }

    private ModelObjectMutationResult AddStructuredDataSource(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "StructuredDataSource");

        var existing = _database.Model.DataSources.FirstOrDefault(d => NameEquals(d.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var dataSource = new StructuredDataSource
        {
            Name = name,
            ConnectionDetails = BuildConnectionDetails(request.SourceType, request.Endpoint, request.SourceDatabase)
        };
        _database.Model.DataSources.Add(dataSource);

        ApplyProperties(dataSource, request.Properties);
        return new ModelObjectMutationResult(dataSource.Name, Changed: true);
    }

    private ModelObjectMutationResult AddRole(string path, ModelObjectAddRequest request)
    {
        var name = RequireModelName(path, request, "Role");

        var existing = _database.Model.Roles.FirstOrDefault(r => NameEquals(r.Name, name));
        if (existing is not null)
            return ExistingOrThrow(existing.Name, request.IfNotExists, path);

        var role = new ModelRole { Name = name, ModelPermission = ModelPermission.Read };
        _database.Model.Roles.Add(role);

        ApplyProperties(role, request.Properties);
        return new ModelObjectMutationResult(role.Name, Changed: true);
    }

    private ModelObjectMutationResult AddTablePermission(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
            throw new InvalidOperationException($"Cannot add a TablePermission at path '{request.Path}'. Expected 'Role/Table'.");

        var role = _database.Model.Roles.FirstOrDefault(r => NameEquals(r.Name, parts[0])) ??
                   throw new InvalidOperationException($"Role not found: {parts[0]}");
        var table = FindTable(parts[1]) ??
                    throw new InvalidOperationException($"Table not found: {parts[1]}");

        var existing = role.TablePermissions.FirstOrDefault(p => NameEquals(p.Name, table.Name));
        if (existing is not null)
            return ExistingOrThrow($"{role.Name}/{existing.Name}", request.IfNotExists, path);

        var permission = new TablePermission
        {
            Name = table.Name,
            Table = table,
            FilterExpression = request.Value ?? ""
        };
        role.TablePermissions.Add(permission);

        ApplyProperties(permission, request.Properties);
        return new ModelObjectMutationResult($"{role.Name}/{permission.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddMember(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
            throw new InvalidOperationException($"Cannot add a Member at path '{request.Path}'. Expected 'Role/Member'.");

        var role = _database.Model.Roles.FirstOrDefault(r => NameEquals(r.Name, parts[0])) ??
                   throw new InvalidOperationException($"Role not found: {parts[0]}");

        var existing = role.Members.FirstOrDefault(m => NameEquals(m.MemberName, parts[1]));
        if (existing is not null)
            return ExistingOrThrow($"{role.Name}/{existing.MemberName}", request.IfNotExists, path);

        var member = new ExternalModelRoleMember
        {
            MemberName = parts[1],
            IdentityProvider = "AzureAD"
        };
        role.Members.Add(member);

        ApplyProperties(member, request.Properties);
        return new ModelObjectMutationResult($"{role.Name}/{member.MemberName}", Changed: true);
    }

    // --- shared helpers --------------------------------------------------------------------

    private (Table Table, string Name) ResolveTableChildTarget(string path, ModelObjectAddRequest request, string typeName)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
            throw new InvalidOperationException($"Cannot add a {typeName} at path '{request.Path}'. Check that -t matches the path shape.");

        var table = FindTable(parts[0]) ??
                    throw new InvalidOperationException($"Table not found: {parts[0]}");
        return (table, parts[1]);
    }

    private static string RequireModelName(string path, ModelObjectAddRequest request, string typeName)
    {
        if (path.Contains('/'))
            throw new InvalidOperationException($"Cannot add a {typeName} at path '{request.Path}'. Check that -t matches the path shape.");
        return path;
    }

    private static ModelObjectMutationResult ExistingOrThrow(string existingPath, bool ifNotExists, string requestedPath)
    {
        if (ifNotExists)
            return new ModelObjectMutationResult(existingPath, Changed: false);
        throw new InvalidOperationException($"Object already exists: {requestedPath}");
    }

    private static void AddColumns(Table table, string? columns)
    {
        if (string.IsNullOrWhiteSpace(columns))
            return;

        foreach (var name in columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (table.Columns.Any(c => NameEquals(c.Name, name)))
                continue;

            table.Columns.Add(new DataColumn
            {
                Name = name,
                DataType = DataType.String,
                SourceColumn = name
            });
        }
    }

    private static ModeType ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return ModeType.Import;

        return Enum.TryParse<ModeType>(mode.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unknown partition mode: {mode}");
    }

    private static string BuildConnectionString(string? endpoint, string? database)
    {
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(database))
            return "Data Source=localhost";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(endpoint))
            parts.Add($"Data Source={endpoint}");
        if (!string.IsNullOrWhiteSpace(database))
            parts.Add($"Initial Catalog={database}");
        return string.Join(";", parts);
    }

    private static ConnectionDetails BuildConnectionDetails(string? sourceType, string? endpoint, string? database)
    {
        var protocol = string.IsNullOrWhiteSpace(sourceType) ? "tds" : sourceType.Trim();
        var details = new ConnectionDetails { Protocol = protocol };
        if (!string.IsNullOrWhiteSpace(endpoint))
            details.Address.Server = endpoint;
        if (!string.IsNullOrWhiteSpace(database))
            details.Address.Database = database;
        return details;
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
}
