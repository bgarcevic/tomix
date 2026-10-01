using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// The TOM objects a live session gives IDs to and reports changes on: exactly the objects
/// <see cref="TomModelSummarizer"/> turns into <see cref="ModelObject"/>s, plus the model root.
/// Kinds and paths here must match the summarizer's; <c>TomObjectTreeTests</c> checks that on
/// the sample models.
/// </summary>
internal static class TomObjectTree
{
    /// <summary>The path of the model root, as mutation paths address it.</summary>
    public const string ModelPath = ".";

    /// <summary>The snapshot kind of a tracked object, or <c>null</c> for anything else
    /// (annotations, translations, perspective entries, expression definitions, ...).</summary>
    public static ModelObjectKind? KindOf(MetadataObject obj) => obj switch
    {
        Model => ModelObjectKind.Model,
        Table => ModelObjectKind.Table,
        Column { Type: ColumnType.RowNumber } => null,
        CalculatedColumn or CalculatedTableColumn => ModelObjectKind.CalculatedColumn,
        Column => ModelObjectKind.Column,
        Measure => ModelObjectKind.Measure,
        KPI => ModelObjectKind.Kpi,
        Hierarchy => ModelObjectKind.Hierarchy,
        Level => ModelObjectKind.Level,
        Partition => ModelObjectKind.Partition,
        RefreshPolicy => ModelObjectKind.RefreshPolicy,
        CalculationItem => ModelObjectKind.CalculationItem,
        Calendar => ModelObjectKind.Calendar,
        Relationship => ModelObjectKind.Relationship,
        ModelRole => ModelObjectKind.Role,
        ModelRoleMember => ModelObjectKind.RoleMember,
        TablePermission => ModelObjectKind.TablePermission,
        Perspective => ModelObjectKind.Perspective,
        Culture => ModelObjectKind.Culture,
        DataSource => ModelObjectKind.DataSource,
        NamedExpression => ModelObjectKind.Expression,
        Function => ModelObjectKind.Function,
        _ => null
    };

    public static bool IsTracked(MetadataObject obj) => KindOf(obj) is not null;

    /// <summary>Whether <paramref name="obj"/> is attached to a model. Objects being built
    /// before they are attached are not, and writes to them are not changes yet.</summary>
    public static bool IsAttached(MetadataObject obj) => obj is Model || obj.Model is not null;

    /// <summary>
    /// The tracked object a write to <paramref name="target"/> changes: the target itself when
    /// tracked, the translated object for a translation, the partition for a partition source,
    /// the model for the database, and otherwise the nearest tracked ancestor.
    /// </summary>
    public static MetadataObject? OwnerOf(object target)
    {
        var current = target switch
        {
            Database database => database.Model,
            PartitionSource source => source.Partition,
            ObjectTranslation translation => translation.Object,
            MetadataObject obj => obj,
            _ => null
        };

        for (; current is not null; current = current.Parent)
        {
            if (IsTracked(current))
                return current;
        }

        return null;
    }

    /// <summary>The canonical path of a tracked object, as <see cref="ModelObject.Path"/>
    /// spells it.</summary>
    public static string PathOf(MetadataObject obj) => obj switch
    {
        Model => ModelPath,
        Table table => Segment(table.Name),
        Column column => Child(column.Table, column.Name),
        Measure measure => Child(measure.Table, measure.Name),
        KPI kpi => $"{PathOf(kpi.Measure)}/KPI",
        Hierarchy hierarchy => Child(hierarchy.Table, hierarchy.Name),
        Level level => $"{PathOf(level.Hierarchy)}/{Segment(level.Name)}",
        Partition partition => Child(partition.Table, partition.Name),
        RefreshPolicy policy => $"{PathOf(policy.Table)}/RefreshPolicy",
        CalculationItem item => Child(item.CalculationGroup.Table, item.Name),
        Calendar calendar => Child(calendar.Table, calendar.Name),
        Relationship relationship => $"Relationships/{Segment(relationship.Name)}",
        ModelRole role => $"Roles/{Segment(role.Name)}",
        ModelRoleMember member => $"{PathOf(member.Role)}/{Segment(member.MemberName)}",
        TablePermission permission => $"{PathOf(permission.Role)}/{Segment(permission.Name)}",
        Perspective perspective => $"Perspectives/{Segment(perspective.Name)}",
        Culture culture => $"Cultures/{Segment(culture.Name)}",
        DataSource dataSource => $"DataSources/{Segment(dataSource.Name)}",
        NamedExpression expression => $"Expressions/{Segment(expression.Name)}",
        Function function => $"Functions/{Segment(function.Name)}",
        _ => throw new ArgumentException($"{obj.GetType().Name} is not a tracked object.", nameof(obj))
    };

    /// <summary>The LineageTag of an object whose kind has one, otherwise <c>null</c>.</summary>
    public static string? LineageTagOf(MetadataObject obj) => obj switch
    {
        Table t => t.LineageTag,
        Column c => c.LineageTag,
        Measure m => m.LineageTag,
        Hierarchy h => h.LineageTag,
        Level l => l.LineageTag,
        NamedExpression e => e.LineageTag,
        Function f => f.LineageTag,
        _ => null
    };

    /// <summary>The model root and every tracked object below it, parents before children,
    /// in a fixed order. Two structurally identical models (a database and its clone) walk in
    /// step, which is how checkpoints pair their objects.</summary>
    public static IEnumerable<MetadataObject> Walk(Model model) => Subtree(model);

    /// <summary><paramref name="root"/> and its tracked descendants, in walk order.</summary>
    public static IEnumerable<MetadataObject> Subtree(MetadataObject root)
    {
        var pending = new Stack<MetadataObject>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            yield return current;
            var children = ChildrenOf(current);
            for (var i = children.Count - 1; i >= 0; i--)
                pending.Push(children[i]);
        }
    }

    private static List<MetadataObject> ChildrenOf(MetadataObject obj)
    {
        var children = new List<MetadataObject>();
        switch (obj)
        {
            case Model model:
                children.AddRange(model.Tables);
                children.AddRange(model.Relationships);
                children.AddRange(model.Roles);
                children.AddRange(model.Perspectives);
                children.AddRange(model.Cultures);
                children.AddRange(model.DataSources);
                children.AddRange(model.Expressions);
                children.AddRange(model.Functions);
                break;
            case Table table:
                children.AddRange(table.Columns.Where(c => c.Type != ColumnType.RowNumber));
                children.AddRange(table.Measures);
                children.AddRange(table.Hierarchies);
                if (table.CalculationGroup is { } calculationGroup)
                    children.AddRange(calculationGroup.CalculationItems);
                children.AddRange(table.Calendars);
                children.AddRange(table.Partitions);
                if (table.RefreshPolicy is { } policy)
                    children.Add(policy);
                break;
            case Measure { KPI: { } kpi }:
                children.Add(kpi);
                break;
            case Hierarchy hierarchy:
                children.AddRange(hierarchy.Levels);
                break;
            case ModelRole role:
                children.AddRange(role.Members);
                children.AddRange(role.TablePermissions);
                break;
        }

        return children;
    }

    private static string Child(Table table, string name) => $"{PathOf(table)}/{Segment(name)}";

    private static string Segment(string name)
        => name.Contains('/') ? $"'{name.Replace("'", "''", StringComparison.Ordinal)}'" : name;
}
