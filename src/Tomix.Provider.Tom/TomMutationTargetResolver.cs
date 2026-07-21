using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using static Tomix.Provider.Tom.TomMutationPaths;

namespace Tomix.Provider.Tom;

/// <summary>A mutation target with its parent (when relevant) and the display path shown to users.</summary>
internal sealed record TomResolvedObject(object Target, object? Parent, string Display);

/// <summary>
/// Resolves mutation paths (DAX forms, slash paths, container-keyword paths, relationship
/// endpoint paths) to concrete TOM objects, enforcing DAX addressing rules and rejecting
/// ambiguous matches.
/// </summary>
internal sealed class TomMutationTargetResolver
{
    private readonly Database _database;

    public TomMutationTargetResolver(Database database) => _database = database;

    internal static ObjectNotFoundException NotFound(string path)
        => new(
            $"Object not found: {path}",
            hint: "Run 'tx ls' to list objects. Quote names containing '/'; pass --type to target a specific object kind.");

    /// <summary>
    /// The object kinds mutation paths can address. A superset of <see cref="ModelObjectKind"/>:
    /// named expressions and functions have container keywords but no public kind.
    /// </summary>
    private enum MutationTargetKind
    {
        Table, Measure, Column, Hierarchy, Partition, CalculationItem, Level,
        Role, RoleMember, Relationship, Perspective, Culture, Expression, Function, DataSource,
        Kpi, TablePermission, Calendar
    }

    /// <summary>
    /// Resolves a mutation target from a path. Accepts DAX forms (<c>'Table'[Child]</c>, restricted
    /// to measures/columns like DAX itself), slash paths, container-keyword paths
    /// (<c>tables/Sales/measures/Revenue</c>), and relationship endpoint paths
    /// (<c>'Sales'[Key]-&gt;'Product'[Key]</c>). Throws <see cref="AmbiguousObjectException"/> when
    /// the path matches more than one object and no <paramref name="type"/> narrows it; returns
    /// null when nothing matches.
    /// </summary>
    public TomResolvedObject? TryResolveForMutation(string path, ModelObjectKind? type)
    {
        var trimmed = path.Trim().Trim('/');
        if (trimmed == ".")
            return new TomResolvedObject(_database, null, ".");

        if (trimmed.Contains("->", StringComparison.Ordinal))
            return type is null or ModelObjectKind.Relationship
                ? ResolveRelationshipByEndpoints(trimmed)
                : null;

        var (parts, daxForm, keywordKind) = ParseMutationPath(trimmed);
        if (parts.Count == 0)
            return null;

        var filter = type is { } explicitType ? ToTargetKind(explicitType) : keywordKind;
        var candidates = new List<(MutationTargetKind Kind, TomResolvedObject Resolved)>();

        if (parts.Count == 1)
            CollectModelLevelCandidates(parts[0], filter, daxForm, candidates);
        else if (parts.Count == 2)
            CollectChildCandidates(parts[0], parts[1], filter, daxForm, candidates);
        else if (parts.Count == 3 && !daxForm)
            CollectLevelCandidates(parts[0], parts[1], parts[2], filter, candidates);

        if (candidates.Count > 1)
            throw new AmbiguousObjectException(
                $"Path '{path}' matches multiple objects: {string.Join(", ", candidates.Select(c => c.Kind.ToString().ToLowerInvariant()))}. "
                + "Disambiguate with --type or a container keyword (e.g. 'tables/T/partitions/P').");

        return candidates.Count == 1 ? candidates[0].Resolved : null;
    }

    private void CollectModelLevelCandidates(
        string name,
        MutationTargetKind? filter,
        bool daxForm,
        List<(MutationTargetKind, TomResolvedObject)> candidates)
    {
        var model = _database.Model;

        if (Allows(filter, daxForm, MutationTargetKind.Table) && FindTable(model, name) is { } table)
            candidates.Add((MutationTargetKind.Table, new TomResolvedObject(table, null, Segment(table.Name))));

        if (Allows(filter, daxForm, MutationTargetKind.Role)
            && model.Roles.FirstOrDefault(r => NameEquals(r.Name, name)) is { } role)
            candidates.Add((MutationTargetKind.Role, new TomResolvedObject(role, null, role.Name)));

        if (Allows(filter, daxForm, MutationTargetKind.Relationship)
            && model.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => NameEquals(r.Name, name)) is { } relationship)
            candidates.Add((MutationTargetKind.Relationship, new TomResolvedObject(relationship, null, RelationshipDisplay(relationship))));

        if (Allows(filter, daxForm, MutationTargetKind.Expression)
            && model.Expressions.FirstOrDefault(e => NameEquals(e.Name, name)) is { } expression)
            candidates.Add((MutationTargetKind.Expression, new TomResolvedObject(expression, null, expression.Name)));

        if (Allows(filter, daxForm, MutationTargetKind.Function)
            && model.Functions.FirstOrDefault(f => NameEquals(f.Name, name)) is { } function)
            candidates.Add((MutationTargetKind.Function, new TomResolvedObject(function, null, function.Name)));

        if (Allows(filter, daxForm, MutationTargetKind.Perspective)
            && model.Perspectives.FirstOrDefault(p => NameEquals(p.Name, name)) is { } perspective)
            candidates.Add((MutationTargetKind.Perspective, new TomResolvedObject(perspective, null, perspective.Name)));

        if (Allows(filter, daxForm, MutationTargetKind.Culture)
            && model.Cultures.FirstOrDefault(c => NameEquals(c.Name, name)) is { } culture)
            candidates.Add((MutationTargetKind.Culture, new TomResolvedObject(culture, null, culture.Name)));

        if (Allows(filter, daxForm, MutationTargetKind.DataSource)
            && model.DataSources.FirstOrDefault(d => NameEquals(d.Name, name)) is { } dataSource)
            candidates.Add((MutationTargetKind.DataSource, new TomResolvedObject(dataSource, null, dataSource.Name)));
    }

    private void CollectChildCandidates(
        string parent,
        string name,
        MutationTargetKind? filter,
        bool daxForm,
        List<(MutationTargetKind, TomResolvedObject)> candidates)
    {
        if (FindTable(_database.Model, parent) is { } table)
        {
            var tablePath = Segment(table.Name);

            if (Allows(filter, daxForm, MutationTargetKind.Measure)
                && table.Measures.FirstOrDefault(m => NameEquals(m.Name, name)) is { } measure)
                candidates.Add((MutationTargetKind.Measure, new TomResolvedObject(measure, table, $"{tablePath}/{Segment(measure.Name)}")));

            if (Allows(filter, daxForm, MutationTargetKind.Column)
                && table.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber && NameEquals(c.Name, name)) is { } column)
                candidates.Add((MutationTargetKind.Column, new TomResolvedObject(column, table, $"{tablePath}/{Segment(column.Name)}")));

            if (Allows(filter, daxForm, MutationTargetKind.Hierarchy)
                && table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, name)) is { } hierarchy)
                candidates.Add((MutationTargetKind.Hierarchy, new TomResolvedObject(hierarchy, table, $"{tablePath}/{Segment(hierarchy.Name)}")));

            if (Allows(filter, daxForm, MutationTargetKind.Partition)
                && table.Partitions.FirstOrDefault(p => NameEquals(p.Name, name)) is { } partition)
                candidates.Add((MutationTargetKind.Partition, new TomResolvedObject(partition, table, $"{tablePath}/{Segment(partition.Name)}")));

            if (Allows(filter, daxForm, MutationTargetKind.CalculationItem)
                && table.CalculationGroup?.CalculationItems.FirstOrDefault(i => NameEquals(i.Name, name)) is { } item)
                candidates.Add((MutationTargetKind.CalculationItem, new TomResolvedObject(item, table, $"{tablePath}/{Segment(item.Name)}")));

            // A KPI has no name of its own — 'Table/Measure' always names the measure first, so
            // the KPI is only reachable with an explicit kind (--type kpi or a KPIs keyword).
            if (filter == MutationTargetKind.Kpi && !daxForm
                && table.Measures.FirstOrDefault(m => NameEquals(m.Name, name)) is { KPI: not null } kpiMeasure)
                candidates.Add((MutationTargetKind.Kpi, new TomResolvedObject(kpiMeasure.KPI, kpiMeasure, $"{tablePath}/{Segment(kpiMeasure.Name)}")));

            if (Allows(filter, daxForm, MutationTargetKind.Calendar)
                && table.Calendars.FirstOrDefault(c => NameEquals(c.Name, name)) is { } calendar)
                candidates.Add((MutationTargetKind.Calendar, new TomResolvedObject(calendar, table, $"{tablePath}/{Segment(calendar.Name)}")));
        }

        if (_database.Model.Roles.FirstOrDefault(r => NameEquals(r.Name, parent)) is { } role)
        {
            if (Allows(filter, daxForm, MutationTargetKind.RoleMember)
                && role.Members.FirstOrDefault(m => NameEquals(m.MemberName, name)) is { } member)
                candidates.Add((MutationTargetKind.RoleMember, new TomResolvedObject(member, role, $"{role.Name}/{member.MemberName}")));

            if (Allows(filter, daxForm, MutationTargetKind.TablePermission)
                && role.TablePermissions.FirstOrDefault(p => NameEquals(p.Name, name)) is { } permission)
                candidates.Add((MutationTargetKind.TablePermission, new TomResolvedObject(permission, role, $"{role.Name}/{permission.Name}")));
        }
    }

    private void CollectLevelCandidates(
        string tableName,
        string hierarchyName,
        string levelName,
        MutationTargetKind? filter,
        List<(MutationTargetKind, TomResolvedObject)> candidates)
    {
        if (!Allows(filter, daxForm: false, MutationTargetKind.Level))
            return;

        if (FindTable(_database.Model, tableName) is { } table
            && table.Hierarchies.FirstOrDefault(h => NameEquals(h.Name, hierarchyName)) is { } hierarchy
            && hierarchy.Levels.FirstOrDefault(l => NameEquals(l.Name, levelName)) is { } level)
            candidates.Add((MutationTargetKind.Level, new TomResolvedObject(
                level, hierarchy, $"{Segment(table.Name)}/{Segment(hierarchy.Name)}/{Segment(level.Name)}")));
    }

    /// <summary>
    /// DAX bracket paths can only address measures and columns — a partition or hierarchy that
    /// happens to share a name must never be picked for <c>'Table'[Child]</c> (a partition hit
    /// would let a measure-looking set replace the partition's M source query).
    /// </summary>
    private static bool Allows(MutationTargetKind? filter, bool daxForm, MutationTargetKind kind)
    {
        if (daxForm && kind is not (MutationTargetKind.Measure or MutationTargetKind.Column))
            return false;

        return filter is null || filter == kind;
    }

    private TomResolvedObject? ResolveRelationshipByEndpoints(string path)
    {
        var match = RelationshipPath().Match(path);
        if (!match.Success)
            throw new ArgumentException(
                "Relationship paths use 'FromTable'[FromColumn]->'ToTable'[ToColumn], e.g. Sales[Key]->Product[Key].");

        var ft = match.Groups["ft"].Value.Trim();
        var fc = match.Groups["fc"].Value.Trim();
        var tt = match.Groups["tt"].Value.Trim();
        var tc = match.Groups["tc"].Value.Trim();

        var relationship = _database.Model.Relationships
            .OfType<SingleColumnRelationship>()
            .FirstOrDefault(r => EndpointEquals(r.FromColumn, ft, fc) && EndpointEquals(r.ToColumn, tt, tc));

        return relationship is null
            ? null
            : new TomResolvedObject(relationship, null, RelationshipDisplay(relationship));

        static bool EndpointEquals(Column column, string tableName, string columnName)
            => column is not null
               && NameEquals(column.Table.Name, tableName)
               && NameEquals(column.Name, columnName);
    }

    /// <summary>
    /// Splits a mutation path into name parts. DAX forms keep quoted table names intact
    /// (doubled <c>''</c> unescapes to a literal apostrophe); slash paths go through
    /// <see cref="ObjectPath.Parse"/> so quoting rules match ls/get. Container keyword segments
    /// are stripped and the deepest one becomes the inferred kind, mirroring add-path inference.
    /// </summary>
    private static (IReadOnlyList<string> Parts, bool DaxForm, MutationTargetKind? KeywordKind) ParseMutationPath(string path)
    {
        var dax = DaxObjectPath().Match(path);
        if (dax.Success)
        {
            var table = dax.Groups["qtable"].Success
                ? dax.Groups["qtable"].Value.Replace("''", "'", StringComparison.Ordinal)
                : dax.Groups["table"].Value;
            return ([table.Trim(), dax.Groups["object"].Value.Trim()], true, null);
        }

        MutationTargetKind? keywordKind = null;
        var parts = new List<string>();
        foreach (var segment in ObjectPath.Parse(path))
        {
            if (segment.TryGetKeyword(out var kind))
                keywordKind = ToTargetKind(kind) ?? keywordKind;
            else if (!segment.IsQuoted && SetSupplementalKeywords.TryGetValue(segment.Text, out var supplemental))
                keywordKind = supplemental;
            else
                parts.Add(segment.Text);
        }

        return (parts, false, keywordKind);
    }

    private static MutationTargetKind? ToTargetKind(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table => MutationTargetKind.Table,
        ModelObjectKind.Measure => MutationTargetKind.Measure,
        ModelObjectKind.Column => MutationTargetKind.Column,
        ModelObjectKind.Hierarchy => MutationTargetKind.Hierarchy,
        ModelObjectKind.Level => MutationTargetKind.Level,
        ModelObjectKind.Partition => MutationTargetKind.Partition,
        ModelObjectKind.Relationship => MutationTargetKind.Relationship,
        ModelObjectKind.Role => MutationTargetKind.Role,
        ModelObjectKind.RoleMember => MutationTargetKind.RoleMember,
        ModelObjectKind.Perspective => MutationTargetKind.Perspective,
        ModelObjectKind.Culture => MutationTargetKind.Culture,
        ModelObjectKind.CalculationItem => MutationTargetKind.CalculationItem,
        ModelObjectKind.DataSource => MutationTargetKind.DataSource,
        ModelObjectKind.Kpi => MutationTargetKind.Kpi,
        ModelObjectKind.TablePermission => MutationTargetKind.TablePermission,
        ModelObjectKind.Calendar => MutationTargetKind.Calendar,
        _ => null
    };

    /// <summary>
    /// Container keywords for mutation paths beyond <see cref="PathSegment"/>'s core set,
    /// matching the supplemental keywords add-path inference accepts.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, MutationTargetKind> SetSupplementalKeywords =
        new Dictionary<string, MutationTargetKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["CalculationGroups"] = MutationTargetKind.Table,
            ["CalcGroups"] = MutationTargetKind.Table,
            ["CalculationItems"] = MutationTargetKind.CalculationItem,
            ["CalcItems"] = MutationTargetKind.CalculationItem,
            ["Expressions"] = MutationTargetKind.Expression,
            ["Functions"] = MutationTargetKind.Function,
            ["DataSources"] = MutationTargetKind.DataSource,
            // Singular KPI included so the ls path 'Table/Measure/KPI' resolves as-is.
            ["KPIs"] = MutationTargetKind.Kpi,
            ["KPI"] = MutationTargetKind.Kpi,
            ["TablePermissions"] = MutationTargetKind.TablePermission,
            ["Calendars"] = MutationTargetKind.Calendar
        };
}
