using Microsoft.AnalysisServices.Tabular;
using Mdl.Core.Models;

namespace Mdl.Provider.Tom;

public static class TomModelSummarizer
{
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

    /// <summary>
    /// Builds a provider-agnostic, navigable snapshot of the model: tables (with their columns,
    /// measures, hierarchies/levels and partitions) plus the model-level relationships, roles
    /// (with members), perspectives and cultures. Every node carries its fully qualified path.
    /// </summary>
    public static ModelSnapshot Snapshot(Database database, string name)
    {
        var model = database.Model;

        var objects = new List<ModelObject>();
        objects.AddRange(model.Tables.Select(BuildTable));
        objects.AddRange(model.Relationships.Select(BuildRelationship));
        objects.AddRange(model.Roles.Select(BuildRole));
        objects.AddRange(model.Perspectives.Select(p =>
            Leaf(p.Name, ModelObjectKind.Perspective, $"Perspectives/{Segment(p.Name)}", detail: null,
                description: Desc(p.Description))));
        objects.AddRange(model.Cultures.Select(c =>
            Leaf(c.Name, ModelObjectKind.Culture, $"Cultures/{Segment(c.Name)}", detail: null)));

        return new ModelSnapshot(name, database.CompatibilityLevel, objects);
    }

    private static ModelObject BuildTable(Table table)
    {
        var path = Segment(table.Name);
        var children = new List<ModelObject>();

        children.AddRange(table.Columns
            .Where(c => c.Type != ColumnType.RowNumber)
            .Select(c => Leaf(
                c.Name,
                ModelObjectKind.Column,
                $"{path}/{Segment(c.Name)}",
                detail: ColumnDetail(c),
                description: Desc(c.Description),
                hidden: c.IsHidden)));

        children.AddRange(table.Measures.Select(m => new ModelObject(
            m.Name,
            ModelObjectKind.Measure,
            $"{path}/{Segment(m.Name)}",
            Detail: null,
            Expression: m.Expression,
            Description: Desc(m.Description),
            Hidden: m.IsHidden,
            Children: [])));

        children.AddRange(table.Hierarchies.Select(h => BuildHierarchy(h, path)));

        children.AddRange(table.Partitions.Select(p => Leaf(
            p.Name,
            ModelObjectKind.Partition,
            $"{path}/{Segment(p.Name)}",
            detail: PartitionDetail(p))));

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
            Children: children);
    }

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
            Children: levels);
    }

    private static ModelObject BuildRelationship(Relationship relationship)
    {
        var path = $"Relationships/{Segment(relationship.Name)}";

        if (relationship is not SingleColumnRelationship single)
            return Leaf(relationship.Name, ModelObjectKind.Relationship, path, detail: null);

        var name = $"{single.FromColumn.Table.Name}[{single.FromColumn.Name}] -> " +
                   $"{single.ToColumn.Table.Name}[{single.ToColumn.Name}]";
        var detail = $"{name} ({Cardinality(single)}, {(single.IsActive ? "active" : "inactive")})";

        return new ModelObject(
            name,
            ModelObjectKind.Relationship,
            path,
            Detail: detail,
            Expression: null,
            Description: null,
            Hidden: false,
            Children: []);
    }

    private static ModelObject BuildRole(ModelRole role)
    {
        var path = $"Roles/{Segment(role.Name)}";
        var members = role.Members
            .Select(m => Leaf(
                m.MemberName,
                ModelObjectKind.RoleMember,
                $"{path}/{Segment(m.MemberName)}",
                detail: null))
            .ToList();

        return new ModelObject(
            role.Name,
            ModelObjectKind.Role,
            path,
            Detail: role.ModelPermission.ToString(),
            Expression: null,
            Description: Desc(role.Description),
            Hidden: false,
            Children: members);
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
        => new(name, kind, path, detail, Expression: null, description, hidden, Children: []);

    // Treat empty/whitespace descriptions as absent so they don't widen the output table.
    private static string? Desc(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    // Quote a path segment when its name contains a space or slash, so the emitted path can be
    // copied back as an object-path filter.
    private static string Segment(string name)
        => name.Contains(' ') || name.Contains('/') ? $"'{name}'" : name;
}
