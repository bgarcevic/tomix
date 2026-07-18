using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using static Tomix.Provider.Tom.TomMutationPaths;

namespace Tomix.Provider.Tom;

/// <summary>
/// Object-creation logic for <see cref="TomModelMutator"/>. Every advertised <c>tx add --type</c>
/// value maps to a builder here that creates the corresponding TOM object under the requested path
/// and reports <c>Changed=true</c>. Builders honor <c>--if-not-exists</c> and the partition/data
/// source options (mode, expression, columns, connection details).
/// </summary>
internal sealed class TomObjectAdder
{
    private readonly Database _database;

    public TomObjectAdder(Database database) => _database = database;

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
    {
        // Relationship paths ('Sales'[Key]->'Product'[Key]) carry quotes and brackets that
        // NormalizePath/ObjectPath would mangle, so they are detected and routed up front.
        if (TryResolveRelationshipPath(request) is { } relationshipPath)
        {
            ValidateAddOptions("relationship", "Relationship", request);
            return AddRelationship(relationshipPath, request);
        }

        var (effectiveType, effectivePath) = ResolveTypeAndPath(request.Type, request.Path);
        var type = NormalizeType(effectiveType);
        if (type.Length > 0)
            ValidateAddOptions(type, effectiveType!.Trim(), request);
        var path = NormalizePath(effectivePath);

        return type switch
        {
            "table" => AddTable(path, request),
            "calctable" => AddCalcTable(path, request),
            "calcgroup" => AddCalcGroup(path, request),
            "measure" => AddMeasure(path, request),
            "calccolumn" => AddCalcColumn(path, request),
            "datacolumn" => AddDataColumn(path, request),
            "hierarchy" => AddHierarchy(path, request),
            "level" => AddLevel(path, request),
            "calendar" => AddCalendar(path, request),
            "calcitem" => AddCalcItem(path, request),
            "kpi" => AddKpi(path, request),
            "partition" => AddPartition(path, request, PartitionKind.M),
            "mpartition" => AddPartition(path, request, PartitionKind.M),
            "entitypartition" => AddPartition(path, request, PartitionKind.Entity),
            "policyrangepartition" => AddPartition(path, request, PartitionKind.PolicyRange),
            "expression" => AddExpression(path, request),
            "function" => AddFunction(path, request),
            "perspective" => AddPerspective(path, request),
            "culture" => AddCulture(path, request),
            "providerdatasource" => AddProviderDataSource(path, request),
            "structureddatasource" => AddStructuredDataSource(path, request),
            "role" => AddRole(path, request),
            "tablepermission" => AddTablePermission(path, request),
            "member" => AddMember(path, request),
            "" => throw new ArgumentException(
                $"No object type given for '{request.Path}'. Pass -t <type> or use a path keyword "
                + "(e.g. 'tables/<Table>', 'tables/<Table>/measures/<Name>')."),
            _ => throw new NotSupportedException($"Adding object type '{request.Type}' is not supported yet.")
        };
    }

    private enum PartitionKind
    {
        M,
        Entity,
        PolicyRange
    }

    /// <summary>
    /// Which add options each object type consumes. Any option supplied for a type not listed here
    /// hard-errors instead of being silently dropped, so the user always learns their input was unusable.
    /// </summary>
    private static readonly (string Option, Func<ModelObjectAddRequest, string?> Get, string[] Types, string AppliesTo)[]
        AddOptionConsumers =
    [
        ("--columns", r => r.Columns, ["table"], "Table"),
        ("--mode", r => r.Mode,
            ["table", "calctable", "partition", "mpartition", "entitypartition", "policyrangepartition"],
            "Table, CalcTable, and partitions"),
        ("--partition-expression", r => r.PartitionExpression,
            ["table", "calctable", "partition", "mpartition"],
            "Table, CalcTable, Partition, MPartition"),
        ("--source", r => r.Source, ["providerdatasource"], "ProviderDataSource"),
        ("--endpoint", r => r.Endpoint,
            ["providerdatasource", "structureddatasource"], "ProviderDataSource, StructuredDataSource"),
        ("--connection-string", r => r.ConnectionString, ["providerdatasource"], "ProviderDataSource"),
        ("--source-table", r => r.SourceTable, ["entitypartition"], "EntityPartition"),
        ("--source-database", r => r.SourceDatabase,
            ["providerdatasource", "structureddatasource"], "ProviderDataSource, StructuredDataSource"),
        ("--source-schema", r => r.SourceSchema, ["entitypartition"], "EntityPartition"),
        ("--source-type", r => r.SourceType, ["structureddatasource"], "StructuredDataSource"),
        ("--range-start", r => r.RangeStart, ["policyrangepartition"], "PolicyRangePartition"),
        ("--range-end", r => r.RangeEnd, ["policyrangepartition"], "PolicyRangePartition"),
        ("--range-granularity", r => r.RangeGranularity, ["policyrangepartition"], "PolicyRangePartition")
    ];

    private static void ValidateAddOptions(string type, string displayType, ModelObjectAddRequest request)
    {
        foreach (var (option, get, types, appliesTo) in AddOptionConsumers)
        {
            if (string.IsNullOrWhiteSpace(get(request)) || types.Contains(type))
                continue;

            var message = $"{option} is not supported for type '{displayType}'. It applies to: {appliesTo}.";
            if (option == "--source-database" && type == "entitypartition")
                message += " For an entity partition schema use --source-schema.";
            throw new UnsupportedAddOptionException(message);
        }
    }

    private Table? FindTable(string name) => TomMutationPaths.FindTable(_database.Model, name);

    private ModelObjectMutationResult AddTable(string path, ModelObjectAddRequest request)
    {
        if (path.Contains('/'))
            throw new InvalidOperationException($"Cannot add a Table at path '{request.Path}'. Check that -t matches the path shape.");

        var existing = FindTable(path);
        if (existing is not null)
        {
            if (request.IfNotExists)
                return new ModelObjectMutationResult(existing.Name, Changed: false);

            throw new InvalidOperationException($"Object already exists: {path}");
        }

        var table = new Table { Name = path };
        table.Partitions.Add(new Partition
        {
            Name = path,
            Mode = ParseMode(request.Mode),
            Source = new MPartitionSource
            {
                Expression = request.PartitionExpression ?? "let Source = #table({}, {}) in Source"
            }
        });
        AddColumns(table, request.Columns);
        _database.Model.Tables.Add(table);

        TomPropertyApplier.ApplyProperties(table, request.Properties);
        return new ModelObjectMutationResult(table.Name, Changed: true);
    }

    private ModelObjectMutationResult AddMeasure(string path, ModelObjectAddRequest request)
    {
        var parts = SplitObjectPath(path);
        if (parts.Count != 2)
        {
            throw parts.Count < 2
                ? new InvalidOperationException(
                    $"Measures require a table parent. Use 'tables/<Table>/measures/<Name>'. Path was '{request.Path}'.")
                : new InvalidOperationException($"Cannot add a Measure at path '{request.Path}'. Expected '<Table>/<Measure>'.");
        }

        var table = FindTable(parts[0]) ??
                    throw new InvalidOperationException($"Table not found: {parts[0]}");

        var existing = table.Measures.FirstOrDefault(m => NameEquals(m.Name, parts[1]));
        if (existing is not null)
        {
            if (request.IfNotExists)
                return new ModelObjectMutationResult($"{table.Name}/{existing.Name}", Changed: false);

            throw new InvalidOperationException($"Object already exists: {path}");
        }

        ThrowIfTableNamespaceCollision(table, parts[1], "measures");

        var measure = new Measure
        {
            Name = parts[1],
            Expression = request.Value ?? ""
        };
        table.Measures.Add(measure);

        TomPropertyApplier.ApplyProperties(measure, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{measure.Name}", Changed: true);
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

        TomPropertyApplier.ApplyProperties(table, request.Properties);
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

        TomPropertyApplier.ApplyProperties(table, request.Properties);
        return new ModelObjectMutationResult(table.Name, Changed: true);
    }

    private ModelObjectMutationResult AddCalcColumn(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "CalcColumn");

        var existing = table.Columns.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);
        ThrowIfTableNamespaceCollision(table, name, "columns");

        var column = new CalculatedColumn
        {
            Name = name,
            Expression = request.Value ?? ""
        };
        table.Columns.Add(column);

        TomPropertyApplier.ApplyProperties(column, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{column.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddDataColumn(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "DataColumn");

        var existing = table.Columns.FirstOrDefault(c => NameEquals(c.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);
        ThrowIfTableNamespaceCollision(table, name, "columns");

        var column = new DataColumn
        {
            Name = name,
            DataType = DataType.String,
            SourceColumn = request.Value ?? name
        };
        table.Columns.Add(column);

        TomPropertyApplier.ApplyProperties(column, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{column.Name}", Changed: true);
    }

    private ModelObjectMutationResult AddHierarchy(string path, ModelObjectAddRequest request)
    {
        var (table, name) = ResolveTableChildTarget(path, request, "Hierarchy");

        var existing = table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, name));
        if (existing is not null)
            return ExistingOrThrow($"{table.Name}/{existing.Name}", request.IfNotExists, path);
        ThrowIfTableNamespaceCollision(table, name, "hierarchies");

        var hierarchy = new Hierarchy { Name = name };
        // A hierarchy needs at least one level to serialize; seed it from the first real column.
        var seed = table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber);
        if (seed is not null)
            hierarchy.Levels.Add(new Level { Name = seed.Name, Ordinal = 0, Column = seed });
        table.Hierarchies.Add(hierarchy);

        TomPropertyApplier.ApplyProperties(hierarchy, request.Properties);
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

        TomPropertyApplier.ApplyProperties(level, request.Properties);
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

        TomPropertyApplier.ApplyProperties(calendar, request.Properties);
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

        TomPropertyApplier.ApplyProperties(item, request.Properties);
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

        TomPropertyApplier.ApplyProperties(measure.KPI, request.Properties);
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

        TomPropertyApplier.ApplyProperties(partition, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{partition.Name}", Changed: true);
    }

    private static PartitionSource BuildPartitionSource(PartitionKind kind, string name, ModelObjectAddRequest request)
        => kind switch
        {
            PartitionKind.Entity => new EntityPartitionSource
            {
                EntityName = request.SourceTable ?? request.Value ?? name,
                SchemaName = string.IsNullOrWhiteSpace(request.SourceSchema) ? null : request.SourceSchema
            },
            PartitionKind.PolicyRange => BuildPolicyRangeSource(request),
            _ => new MPartitionSource
            {
                Expression = request.PartitionExpression ?? request.Value ?? "let Source = #table({}, {}) in Source"
            }
        };

    private static PolicyRangePartitionSource BuildPolicyRangeSource(ModelObjectAddRequest request)
    {
        var start = ParseRangeDate(request.RangeStart, "--range-start");
        var end = ParseRangeDate(request.RangeEnd, "--range-end");
        if (start >= end)
            throw new ArgumentException($"--range-start must be before --range-end ({request.RangeStart} >= {request.RangeEnd}).");

        return new PolicyRangePartitionSource
        {
            Start = start,
            End = end,
            Granularity = ParseRangeGranularity(request.RangeGranularity)
        };
    }

    private static DateTime ParseRangeDate(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("PolicyRangePartition requires --range-start and --range-end (yyyy-MM-dd).");

        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
            return exact;

        if (DateTime.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        throw new ArgumentException($"Invalid date for {option}: '{value}'. Use yyyy-MM-dd.");
    }

    private static RefreshGranularityType ParseRangeGranularity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return RefreshGranularityType.Day;

        if (Enum.TryParse<RefreshGranularityType>(value.Trim(), ignoreCase: true, out var parsed)
            && parsed != RefreshGranularityType.Invalid)
            return parsed;

        throw new ArgumentException($"Unknown range granularity: '{value}'. Known values: Day, Month, Quarter, Year.");
    }

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

        TomPropertyApplier.ApplyProperties(expression, request.Properties);
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

        TomPropertyApplier.ApplyProperties(function, request.Properties);
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

        TomPropertyApplier.ApplyProperties(perspective, request.Properties);
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

        TomPropertyApplier.ApplyProperties(culture, request.Properties);
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

        TomPropertyApplier.ApplyProperties(dataSource, request.Properties);
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

        TomPropertyApplier.ApplyProperties(dataSource, request.Properties);
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

        TomPropertyApplier.ApplyProperties(role, request.Properties);
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

        TomPropertyApplier.ApplyProperties(permission, request.Properties);
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

        TomPropertyApplier.ApplyProperties(member, request.Properties);
        return new ModelObjectMutationResult($"{role.Name}/{member.MemberName}", Changed: true);
    }

    /// <summary>
    /// Detects a relationship add and returns the arrow path to build from, or null when the
    /// request targets a different object type. Accepts an explicit <c>-t relationship</c>, a
    /// <c>relationships/</c> keyword prefix, or a typeless path containing <c>-&gt;</c>.
    /// </summary>
    private static string? TryResolveRelationshipPath(ModelObjectAddRequest request)
    {
        var type = NormalizeType(request.Type);
        var path = request.Path.Trim();

        const string keywordPrefix = "relationships/";
        if (path.StartsWith(keywordPrefix, StringComparison.OrdinalIgnoreCase))
            return type is "" or "relationship" ? path[keywordPrefix.Length..].Trim() : null;

        if (type == "relationship")
            return path;

        return type.Length == 0 && path.Contains("->", StringComparison.Ordinal) ? path : null;
    }

    private ModelObjectMutationResult AddRelationship(string path, ModelObjectAddRequest request)
    {
        var match = RelationshipPath().Match(path);
        if (!match.Success)
            throw new ArgumentException(
                "Relationship paths use 'FromTable'[FromColumn]->'ToTable'[ToColumn], e.g. Sales[Key]->Product[Key].");

        var fromColumn = ResolveRelationshipColumn(match.Groups["ft"].Value.Trim(), match.Groups["fc"].Value.Trim());
        var toColumn = ResolveRelationshipColumn(match.Groups["tt"].Value.Trim(), match.Groups["tc"].Value.Trim());

        var existing = _database.Model.Relationships
            .OfType<SingleColumnRelationship>()
            .FirstOrDefault(r => r.FromColumn == fromColumn && r.ToColumn == toColumn);
        if (existing is not null)
            return ExistingOrThrow(RelationshipDisplay(existing), request.IfNotExists, path);

        var relationship = new SingleColumnRelationship
        {
            // TOM relationships are GUID-named (the Power BI Desktop convention); the display
            // path below is what users see and what `tx ls relationships` shows.
            Name = Guid.NewGuid().ToString(),
            FromColumn = fromColumn,
            ToColumn = toColumn,
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        };
        _database.Model.Relationships.Add(relationship);

        TomPropertyApplier.ApplyProperties(relationship, request.Properties);
        return new ModelObjectMutationResult(RelationshipDisplay(relationship), Changed: true);
    }

    private Column ResolveRelationshipColumn(string tableName, string columnName)
    {
        var table = FindTable(tableName) ??
                    throw new InvalidOperationException($"Table not found: {tableName}");
        return table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber && NameEquals(c.Name, columnName)) ??
               throw new InvalidOperationException($"Column not found: {table.Name}[{columnName}]");
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

    /// <summary>
    /// Measures, columns, and hierarchies share one namespace within a table — the engine
    /// rejects a model where two of them carry the same name, so adds must fail up front
    /// instead of writing TMDL a deploy won't accept. The caller checks its own collection
    /// separately (that duplicate honors <c>--if-not-exists</c>); this guards the cross-kind
    /// collisions, which are always an error.
    /// </summary>
    private static void ThrowIfTableNamespaceCollision(Table table, string name, string ownCollection)
    {
        var colliding =
            ownCollection != "measures" && table.Measures.Any(m => NameEquals(m.Name, name)) ? "measure"
            : ownCollection != "columns" && table.Columns.Any(c => c.Type != ColumnType.RowNumber && NameEquals(c.Name, name)) ? "column"
            : ownCollection != "hierarchies" && table.Hierarchies.Any(h => NameEquals(h.Name, name)) ? "hierarchy"
            : null;

        if (colliding is not null)
            throw new InvalidOperationException(
                $"Object already exists: a {colliding} named '{name}' exists in table '{table.Name}' "
                + "(measures, columns, and hierarchies share a namespace).");
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
}
