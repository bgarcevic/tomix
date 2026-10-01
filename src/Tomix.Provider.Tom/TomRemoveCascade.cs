using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom;

/// <summary>
/// Structural cleanup for removals. TOM does not cascade: removing a table or column leaves
/// relationships, sort-by pointers, hierarchy levels, variations, perspective memberships, role
/// permissions, and translations pointing at the deleted object, and the model then fails
/// validation on update or serialization. Each cleanup returns a short description so the CLI
/// can report what else was removed. Call before detaching the object — the sweeps walk parent
/// chains that removal severs. Every write goes through the caller's <see cref="TomWriter"/>.
/// </summary>
internal static class TomRemoveCascade
{
    public static IReadOnlyList<string> ForTable(TomWriter w, Table table)
    {
        var model = table.Model;
        var removed = new List<string>();

        foreach (var relationship in model.Relationships.OfType<SingleColumnRelationship>()
                     .Where(r => r.FromTable == table || r.ToTable == table).ToList())
            RemoveRelationship(w, model, relationship, removed);

        foreach (var perspective in model.Perspectives)
        {
            if (perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == table) is { } member)
            {
                w.Detach(perspective.PerspectiveTables, member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        foreach (var role in model.Roles)
        {
            if (role.TablePermissions.FirstOrDefault(p => p.Table == table) is { } permission)
            {
                w.Detach(role.TablePermissions, permission);
                removed.Add($"table permission in role '{role.Name}'");
            }
        }

        RemoveVariations(w,
            model,
            v => v.DefaultHierarchy?.Table == table || v.DefaultColumn?.Table == table,
            removed);
        RemoveTranslations(w, model, table, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForColumn(TomWriter w, Column column)
    {
        var table = column.Table;
        var model = table.Model;
        var removed = new List<string>();

        foreach (var relationship in model.Relationships.OfType<SingleColumnRelationship>()
                     .Where(r => r.FromColumn == column || r.ToColumn == column).ToList())
            RemoveRelationship(w, model, relationship, removed);

        foreach (var other in model.Tables.SelectMany(t => t.Columns)
                     .Where(c => c.SortByColumn == column).ToList())
        {
            w.Set(other, c => c.SortByColumn, null);
            removed.Add($"sort-by on {Dax(other)} (cleared)");
        }

        foreach (var hierarchy in table.Hierarchies.ToList())
        {
            foreach (var level in hierarchy.Levels.Where(l => l.Column == column).ToList())
            {
                RemoveTranslations(w, model, level, removed);
                w.Detach(hierarchy.Levels, level);
                removed.Add($"level '{level.Name}' in hierarchy {Dax(table, hierarchy.Name)}");
            }

            if (hierarchy.Levels.Count == 0)
            {
                removed.AddRange(ForHierarchy(w, hierarchy));
                w.Detach(table.Hierarchies, hierarchy);
                removed.Add($"hierarchy {Dax(table, hierarchy.Name)} (no levels left)");
            }
        }

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == table);
            if (perspectiveTable?.PerspectiveColumns.FirstOrDefault(pc => pc.Column == column) is { } member)
            {
                w.Detach(perspectiveTable.PerspectiveColumns, member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        foreach (var role in model.Roles)
            foreach (var permission in role.TablePermissions)
            {
                if (permission.ColumnPermissions.FirstOrDefault(cp => cp.Column == column) is { } columnPermission)
                {
                    w.Detach(permission.ColumnPermissions, columnPermission);
                    removed.Add($"column permission in role '{role.Name}'");
                }
            }

        RemoveVariations(w, model, v => v.DefaultColumn == column, removed);
        RemoveTranslations(w, model, column, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForMeasure(TomWriter w, Measure measure)
    {
        var model = measure.Table.Model;
        var removed = new List<string>();

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == measure.Table);
            if (perspectiveTable?.PerspectiveMeasures.FirstOrDefault(pm => pm.Measure == measure) is { } member)
            {
                w.Detach(perspectiveTable.PerspectiveMeasures, member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        RemoveTranslations(w, model, measure, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForHierarchy(TomWriter w, Hierarchy hierarchy)
    {
        var model = hierarchy.Table.Model;
        var removed = new List<string>();

        foreach (var perspective in model.Perspectives)
        {
            var perspectiveTable = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == hierarchy.Table);
            if (perspectiveTable?.PerspectiveHierarchies.FirstOrDefault(ph => ph.Hierarchy == hierarchy) is { } member)
            {
                w.Detach(perspectiveTable.PerspectiveHierarchies, member);
                removed.Add($"'{perspective.Name}' perspective entry");
            }
        }

        RemoveVariations(w, model, v => v.DefaultHierarchy == hierarchy, removed);
        RemoveTranslations(w, model, hierarchy, removed);
        return removed;
    }

    /// <summary>Cleanup for removing the relationship itself (the caller detaches it).</summary>
    public static IReadOnlyList<string> ForRelationship(TomWriter w, SingleColumnRelationship relationship)
    {
        var removed = new List<string>();
        RemoveVariations(w, relationship.Model, v => v.Relationship == relationship, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForLevel(TomWriter w, Level level)
    {
        var removed = new List<string>();
        RemoveTranslations(w, level.Hierarchy.Table.Model, level, removed);
        return removed;
    }

    public static IReadOnlyList<string> ForCalculationItem(TomWriter w, CalculationItem item)
    {
        var removed = new List<string>();
        RemoveTranslations(w, item.CalculationGroup.Table.Model, item, removed);
        return removed;
    }

    private static void RemoveRelationship(TomWriter w, Model model, SingleColumnRelationship relationship, List<string> removed)
    {
        // Variations (auto date/time) bind to a relationship and dangle when it goes.
        RemoveVariations(w, model, v => v.Relationship == relationship, removed);
        w.Detach(model.Relationships, relationship);
        removed.Add($"relationship {Dax(relationship.FromColumn)} -> {Dax(relationship.ToColumn)}");
    }

    private static void RemoveVariations(TomWriter w, Model model, Func<Variation, bool> dangles, List<string> removed)
    {
        foreach (var column in model.Tables.SelectMany(t => t.Columns))
            foreach (var variation in column.Variations.Where(dangles).ToList())
            {
                w.Detach(column.Variations, variation);
                removed.Add($"variation on {Dax(column)}");
            }
    }

    private static void RemoveTranslations(TomWriter w, Model model, MetadataObject root, List<string> removed)
    {
        foreach (var culture in model.Cultures)
        {
            var dangling = culture.ObjectTranslations
                .Where(t => IsSelfOrDescendant(t.Object, root))
                .ToList();
            if (dangling.Count == 0)
                continue;

            foreach (var translation in dangling)
                w.Detach(culture.ObjectTranslations, translation);
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
