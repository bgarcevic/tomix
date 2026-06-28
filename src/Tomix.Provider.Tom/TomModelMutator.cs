using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;

namespace Tomix.Provider.Tom;

public sealed partial class TomModelMutator
{
    private readonly Database _database;

    public TomModelMutator(Database database) => _database = database;

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
    {
        var (effectiveType, effectivePath) = ResolveTypeAndPath(request.Type, request.Path);
        var type = NormalizeType(effectiveType);
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

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
    {
        if (request.Properties.Count == 0)
            throw new ArgumentException("At least one property assignment is required.", nameof(request));

        var target = ResolveObject(request.Path, request.Type);
        if (target is null)
            throw new InvalidOperationException($"Object not found: {request.Path}");

        ModelPropertyAssignment last = request.Properties[^1];
        foreach (var assignment in request.Properties)
        {
            ApplyProperty(target.Target, assignment);
            last = assignment;
        }

        return new ModelObjectMutationResult(
            NormalizePath(request.Path),
            Changed: true,
            Property: last.Property,
            Value: last.Value);
    }

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
    {
        var path = NormalizePath(request.Path);
        var target = ResolveObject(path, request.Type);
        if (target is null)
        {
            if (request.IfExists)
                return new ModelObjectMutationResult(path, Changed: false, Reason: "not_found");

            throw new InvalidOperationException($"Object not found: {request.Path}");
        }

        RemoveResolvedObject(target);
        return new ModelObjectMutationResult(path, Changed: true);
    }

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
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

        ApplyProperties(table, request.Properties);
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

        var measure = new Measure
        {
            Name = parts[1],
            Expression = request.Value ?? ""
        };
        table.Measures.Add(measure);

        ApplyProperties(measure, request.Properties);
        return new ModelObjectMutationResult($"{table.Name}/{measure.Name}", Changed: true);
    }

    private void ApplyProperties(object target, IReadOnlyList<ModelPropertyAssignment> properties)
    {
        foreach (var property in properties)
            ApplyProperty(target, property);
    }

    private void ApplyProperty(object target, ModelPropertyAssignment assignment)
    {
        // Annotation names are case-sensitive and their values are opaque (often JSON), so handle
        // them before the property name is normalized/lowercased.
        if (TryApplyAnnotation(target, assignment))
            return;

        var property = NormalizeProperty(assignment.Property);
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
            case Culture culture when property is "name":
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
            default:
                throw new NotSupportedException($"Setting '{assignment.Property}' is not supported for this object.");
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
                throw new NotSupportedException($"Setting '{displayName}' is not supported for tables.");
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
                throw new NotSupportedException($"Setting '{displayName}' is not supported for measures.");
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
                throw new NotSupportedException($"Setting '{displayName}' is not supported for columns.");
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
            default:
                throw new NotSupportedException($"Setting '{displayName}' is not supported for partitions.");
        }
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

    private ResolvedObject? ResolveObject(string path, ModelObjectKind? type)
    {
        var normalized = NormalizePath(path);
        if (normalized == ".")
            return new ResolvedObject(_database, null);

        var parts = SplitObjectPath(normalized);
        if (parts.Count == 0)
            return null;

        var table = FindTable(parts[0]);
        if (table is not null && parts.Count == 1)
            return type is null or ModelObjectKind.Table
                ? new ResolvedObject(table, null)
                : null;

        if (table is not null && parts.Count == 2)
            return ResolveTableChild(table, parts[1], type);

        var role = _database.Model.Roles.FirstOrDefault(r => NameEquals(r.Name, parts[0]));
        if (role is not null && parts.Count == 1)
            return type is null or ModelObjectKind.Role
                ? new ResolvedObject(role, null)
                : null;

        return null;
    }

    private ResolvedObject? ResolveTableChild(Table table, string name, ModelObjectKind? type)
    {
        if (type is null or ModelObjectKind.Measure)
        {
            var measure = table.Measures.FirstOrDefault(m => NameEquals(m.Name, name));
            if (measure is not null)
                return new ResolvedObject(measure, table);
        }

        if (type is null or ModelObjectKind.Column)
        {
            var column = table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber && NameEquals(c.Name, name));
            if (column is not null)
                return new ResolvedObject(column, table);
        }

        if (type is null or ModelObjectKind.Hierarchy)
        {
            var hierarchy = table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, name));
            if (hierarchy is not null)
                return new ResolvedObject(hierarchy, table);
        }

        if (type is null or ModelObjectKind.Partition)
        {
            var partition = table.Partitions.FirstOrDefault(p => NameEquals(p.Name, name));
            if (partition is not null)
                return new ResolvedObject(partition, table);
        }

        return null;
    }

    private void RemoveResolvedObject(ResolvedObject resolved)
    {
        switch (resolved.Target)
        {
            case Table table:
                _database.Model.Tables.Remove(table);
                break;
            case Measure measure when resolved.Parent is Table table:
                table.Measures.Remove(measure);
                break;
            case Column column when resolved.Parent is Table table:
                table.Columns.Remove(column);
                break;
            case Hierarchy hierarchy when resolved.Parent is Table table:
                table.Hierarchies.Remove(hierarchy);
                break;
            case Partition partition when resolved.Parent is Table table:
                table.Partitions.Remove(partition);
                break;
            case ModelRole role:
                _database.Model.Roles.Remove(role);
                break;
            default:
                throw new NotSupportedException("Removing this object type is not supported yet.");
        }
    }

    private IEnumerable<ReplaceOperation> BuildReplaceOperations(ModelReplaceRequest request)
    {
        var scope = NormalizeReplaceScope(request.Scope);

        if (scope is "all" or "names")
        {
            foreach (var operation in BuildNameReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "expressions")
        {
            foreach (var operation in BuildExpressionReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "descriptions")
        {
            foreach (var operation in BuildDescriptionReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "displayfolders")
        {
            foreach (var operation in BuildDisplayFolderReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "formatstrings")
        {
            foreach (var operation in BuildFormatStringReplaceOperations(request))
                yield return operation;
        }
    }

    private IEnumerable<ReplaceOperation> BuildNameReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);

            yield return ReplaceProperty(tablePath, "Name", table.Name, value => table.Name = value, request);

            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Name", measure.Name, value => measure.Name = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Name", column.Name, value => column.Name = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "Name", hierarchy.Name, value => hierarchy.Name = value, request);
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var rolePath = $"Roles/{Segment(role.Name)}";
            yield return ReplaceProperty(rolePath, "Name", role.Name, value => role.Name = value, request);
        }
    }

    private IEnumerable<ReplaceOperation> BuildExpressionReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Expression", measure.Expression, value => measure.Expression = value, request);
            }

            foreach (var column in table.Columns.OfType<CalculatedColumn>())
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Expression", column.Expression, value => column.Expression = value, request);
            }

            foreach (var partition in table.Partitions)
            {
                var partitionPath = $"{tablePath}/Partitions/{Segment(partition.Name)}";
                if (partition.Source is MPartitionSource source)
                    yield return ReplaceProperty(partitionPath, "Expression", source.Expression, value => source.Expression = value, request);
            }
        }
    }

    private IEnumerable<ReplaceOperation> BuildDescriptionReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            yield return ReplaceProperty(tablePath, "Description", table.Description, value => table.Description = value, request);

            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Description", measure.Description, value => measure.Description = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Description", column.Description, value => column.Description = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "Description", hierarchy.Description, value => hierarchy.Description = value, request);
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var rolePath = $"Roles/{Segment(role.Name)}";
            yield return ReplaceProperty(rolePath, "Description", role.Description, value => role.Description = value, request);
        }
    }

    private IEnumerable<ReplaceOperation> BuildDisplayFolderReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "DisplayFolder", measure.DisplayFolder, value => measure.DisplayFolder = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "DisplayFolder", column.DisplayFolder, value => column.DisplayFolder = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "DisplayFolder", hierarchy.DisplayFolder, value => hierarchy.DisplayFolder = value, request);
            }
        }
    }

    private IEnumerable<ReplaceOperation> BuildFormatStringReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "FormatString", measure.FormatString, value => measure.FormatString = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "FormatString", column.FormatString, value => column.FormatString = value, request);
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
            _ => scope.Trim().ToLowerInvariant()
        };
    }

    private Table? FindTable(string name)
        => _database.Model.Tables.FirstOrDefault(t => NameEquals(t.Name, name));

    private static bool NameEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Segment(string name)
        => name.Contains('/') ? $"'{name}'" : name;

    private static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "";

        return type.Trim().ToLowerInvariant() switch
        {
            "calcmeasure" => "measure",
            var normalized => normalized
        };
    }

    /// <summary>
    /// Resolves the effective type and name-only path. When the path uses container keywords
    /// (e.g. <c>tables/Sales/measures/Revenue</c>), keyword segments are stripped and the type is
    /// inferred from the deepest keyword unless <paramref name="type"/> was given explicitly.
    /// Paths without keywords are returned unchanged so existing <c>-t</c>-based usage is unaffected.
    /// </summary>
    private static (string? Type, string Path) ResolveTypeAndPath(string? type, string path)
    {
        var segments = ObjectPath.Parse(path);
        if (segments.Count == 0 || segments.All(s => !s.IsKeyword))
            return (type, path);

        string? lastKeywordType = null;
        var nameSegments = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.TryGetKeyword(out var kind))
            {
                if (TryMapKeywordToTypeName(kind) is { } mapped)
                    lastKeywordType = mapped;
            }
            else
            {
                nameSegments.Add(segment.Text);
            }
        }

        if (nameSegments.Count == 0 || lastKeywordType is null)
            return (type, path);

        var effectiveType = !string.IsNullOrWhiteSpace(type) ? type : lastKeywordType;
        return (effectiveType, string.Join("/", nameSegments));
    }

    private static string? TryMapKeywordToTypeName(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table => "table",
        ModelObjectKind.Measure => "measure",
        ModelObjectKind.Column => "calccolumn",
        ModelObjectKind.Hierarchy => "hierarchy",
        ModelObjectKind.Level => "level",
        ModelObjectKind.Partition => "partition",
        ModelObjectKind.Role => "role",
        ModelObjectKind.RoleMember => "member",
        ModelObjectKind.Perspective => "perspective",
        ModelObjectKind.Culture => "culture",
        _ => null
    };

    private static string NormalizeProperty(string property)
        => property.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('/');
        var dax = DaxObjectPath().Match(trimmed);
        if (dax.Success)
            return $"{dax.Groups["table"].Value}/{dax.Groups["object"].Value}";

        return trimmed.Replace("'", "", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SplitObjectPath(string path)
        => NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool ParseBool(string value, string property)
    {
        if (bool.TryParse(value, out var parsed))
            return parsed;

        if (value == "1")
            return true;
        if (value == "0")
            return false;

        throw new ArgumentException($"Value for '{property}' must be true or false.");
    }

    private static int ParseInt(string value, string property)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;

        throw new ArgumentException($"Value for '{property}' must be an integer.");
    }

    [GeneratedRegex("^'?(?<table>[^'\\[]+)'?\\[(?<object>[^\\]]+)\\]$")]
    private static partial Regex DaxObjectPath();

    private sealed record ResolvedObject(object Target, object? Parent);

    private sealed record ReplaceOperation(
        ModelReplacePreview Preview,
        Action<string> Apply);
}
