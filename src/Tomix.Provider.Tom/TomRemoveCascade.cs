using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom;

/// <summary>
/// Structural cleanup for removals. TOM does not cascade: removing a table or column leaves
/// relationships, sort-by pointers, hierarchy levels, variations, perspective memberships, role
/// permissions, and translations pointing at the deleted object, and the model then fails
/// validation on update or serialization. Each cleanup returns a short description so the CLI
/// can report what else was removed. Call before detaching the object — the sweeps walk parent
/// chains that removal severs.
/// </summary>
internal static class TomRemoveCascade
{
    public static IReadOnlyList<string> ForTable(Table table)
    {
        var model = table.Model;
        var removed = new List<string>();

        foreach (var relationship in model.Relationships.OfType<SingleColumnRelationship>()
                     .Where(r => r.FromTable == table || r.ToTable == table).ToList())
            RemoveRelationship(model, relationship, removed);

        foreach (var perspective in model.Perspectives)
        {
            if (perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == table) is { } member)
            {
                perspective.PerspectiveTables.Remove(member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        foreach (var role in model.Roles)
        {
            if (role.TablePermissions.FirstOrDefault(p => p.Table == table) is { } permission)
            {
                role.TablePermissions.Remove(permission);
                removed.Add($"table permission in role '{role.Name}'");
            }
        }

        RemoveVariations(
            model,
            v => v.DefaultHierarchy?.Table == table || v.DefaultColumn?.Table == table,
            removed);
        RemoveTranslations(model, table, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForColumn(Column column)
    {
        var table = column.Table;
        var model = table.Model;
        var removed = new List<string>();

        foreach (var relationship in model.Relationships.OfType<SingleColumnRelationship>()
                     .Where(r => r.FromColumn == column || r.ToColumn == column).ToList())
            RemoveRelationship(model, relationship, removed);

        foreach (var other in model.Tables.SelectMany(t => t.Columns)
                     .Where(c => c.SortByColumn == column).ToList())
        {
            other.SortByColumn = null;
            removed.Add($"sort-by on {Dax(other)} (cleared)");
        }

        foreach (var hierarchy in table.Hierarchies.ToList())
        {
            foreach (var level in hierarchy.Levels.Where(l => l.Column == column).ToList())
            {
                RemoveTranslations(model, level, removed);
                hierarchy.Levels.Remove(level);
                removed.Add($"level '{level.Name}' in hierarchy {Dax(table, hierarchy.Name)}");
            }

            if (hierarchy.Levels.Count == 0)
            {
                removed.AddRange(ForHierarchy(hierarchy));
                table.Hierarchies.Remove(hierarchy);
                removed.Add($"hierarchy {Dax(table, hierarchy.Name)} (no levels left)");
            }
        }

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == table);
            if (perspectiveTable?.PerspectiveColumns.FirstOrDefault(pc => pc.Column == column) is { } member)
            {
                perspectiveTable.PerspectiveColumns.Remove(member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        foreach (var role in model.Roles)
            foreach (var permission in role.TablePermissions)
            {
                if (permission.ColumnPermissions.FirstOrDefault(cp => cp.Column == column) is { } columnPermission)
                {
                    permission.ColumnPermissions.Remove(columnPermission);
                    removed.Add($"column permission in role '{role.Name}'");
                }
            }

        RemoveVariations(model, v => v.DefaultColumn == column, removed);
        RemoveTranslations(model, column, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForMeasure(Measure measure)
    {
        var model = measure.Table.Model;
        var removed = new List<string>();

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == measure.Table);
            if (perspectiveTable?.PerspectiveMeasures.FirstOrDefault(pm => pm.Measure == measure) is { } member)
            {
                perspectiveTable.PerspectiveMeasures.Remove(member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        RemoveTranslations(model, measure, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForHierarchy(Hierarchy hierarchy)
    {
        var model = hierarchy.Table.Model;
        var removed = new List<string>();

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == hierarchy.Table);
            if (perspectiveTable?.PerspectiveHierarchies.FirstOrDefault(ph => ph.Hierarchy == hierarchy) is { } member)
            {
                perspectiveTable.PerspectiveHierarchies.Remove(member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        RemoveVariations(model, v => v.DefaultHierarchy == hierarchy, removed);
        RemoveTranslations(model, hierarchy, removed);
        return removed;
    }

    /// <summary>Cleanup for removing the relationship itself (the caller detaches it).</summary>
    public static IReadOnlyList<string> ForRelationship(SingleColumnRelationship relationship)
    {
        var removed = new List<string>();
        RemoveVariations(relationship.Model, v => v.Relationship == relationship, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForLevel(Level level)
    {
        var removed = new List<string>();
        RemoveTranslations(level.Hierarchy.Table.Model, level, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForCalculationItem(CalculationItem item)
    {
        var removed = new List<string>();
        RemoveTranslations(item.CalculationGroup.Table.Model, item, removed);
        return removed;
    }

    private static void RemoveRelationship(Model model, SingleColumnRelationship relationship, List<string> removed)
    {
        // Variations (auto date/time) bind to a relationship and dangle when it goes.
        RemoveVariations(model, v => v.Relationship == relationship, removed);
        model.Relationships.Remove(relationship);
        removed.Add($"relationship {Dax(relationship.FromColumn)} -> {Dax(relationship.ToColumn)}");
    }

    private static void RemoveVariations(Model model, Func<Variation, bool> dangles, List<string> removed)
    {
        foreach (var column in model.Tables.SelectMany(t => t.Columns))
            foreach (var variation in column.Variations.Where(dangles).ToList())
            {
                column.Variations.Remove(variation);
                removed.Add($"variation on {Dax(column)}");
            }
    }

    private static void RemoveTranslations(Model model, MetadataObject root, List<string> removed)
    {
        foreach (var culture in model.Cultures)
        {
            var dangling = culture.ObjectTranslations
                .Where(t => IsSelfOrDescendant(t.Object, root))
                .ToList();
            if (dangling.Count == 0)
                continue;

            foreach (var translation in dangling)
                culture.ObjectTranslations.Remove(translation);
            removed.Add($"{dangling.Count} translation(s) in culture '{culture.Name}'");
        }
    }

    private static bool IsSelfOrDescendant(MetadataObject? candidate, MetadataObject root)
    {
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
    }

    private static string Dax(Column column) => $"'{column.Table.Name}'[{column.Name}]";

    private static string Dax(Table table, string child) => $"'{table.Name}'[{child}]";
}
