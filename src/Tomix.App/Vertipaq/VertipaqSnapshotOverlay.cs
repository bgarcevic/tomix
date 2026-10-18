using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.App.Vertipaq;

/// <summary>
/// The outcome of <see cref="VertipaqSnapshotOverlay.Apply"/>: the annotated copy of the snapshot,
/// how many statistics tables found their model table, and the names of those that did not.
/// </summary>
public sealed record VertipaqOverlayResult(
    ModelSnapshot Snapshot,
    int MatchedTables,
    IReadOnlyList<string> UnmatchedTables);

/// <summary>
/// Lays statistics over a snapshot as the same <c>Vertipaq_*</c> annotations
/// <c>tx vertipaq --annotate</c> writes (<see cref="VertipaqAnnotationBuilder"/>), on a copy and in
/// memory only, so BPA statistics rules can read an exported <c>.vpax</c> without the model being
/// changed. Objects match by name, ignoring case as the engine does; statistics win over
/// annotations the model already carries.
/// </summary>
public static class VertipaqSnapshotOverlay
{
    public static VertipaqOverlayResult Apply(ModelSnapshot snapshot, VertipaqModelStats stats)
    {
        var tables = stats.Tables
            .GroupBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var columns = stats.Columns
            .Where(c => !c.IsRowNumber)
            .GroupBy(c => Key(c.TableName, c.ColumnName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var relationships = stats.Relationships
            .GroupBy(r => RelationshipKey(r.FromTable, r.FromColumn, r.ToTable, r.ToColumn), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var objects = snapshot.Objects.Select(o => Overlay(o, table: null)).ToList();
        var overlaid = snapshot with
        {
            Objects = objects,
            Properties = Merge(snapshot.Properties, VertipaqAnnotationBuilder.ForModel(stats))
        };

        var unmatched = stats.Tables
            .Select(t => t.TableName)
            .Where(name => !matched.Contains(name))
            .ToList();
        return new VertipaqOverlayResult(overlaid, matched.Count, unmatched);

        ModelObject Overlay(ModelObject obj, string? table)
        {
            IReadOnlyList<ModelPropertyAssignment>? annotations = null;
            switch (obj.Kind)
            {
                case ModelObjectKind.Table when tables.TryGetValue(obj.Name, out var tableStats):
                    matched.Add(tableStats.TableName);
                    annotations = VertipaqAnnotationBuilder.ForTable(tableStats);
                    break;
                case ModelObjectKind.Column or ModelObjectKind.CalculatedColumn
                    when table is not null && columns.TryGetValue(Key(table, obj.Name), out var columnStats):
                    annotations = VertipaqAnnotationBuilder.ForColumn(columnStats);
                    break;
                case ModelObjectKind.Relationship when relationships.TryGetValue(
                    RelationshipKey(
                        obj.Property("FromTable"), obj.Property("FromColumn"),
                        obj.Property("ToTable"), obj.Property("ToColumn")),
                    out var relationshipStats):
                    annotations = VertipaqAnnotationBuilder.ForRelationship(relationshipStats);
                    break;
            }

            var childTable = obj.Kind == ModelObjectKind.Table ? obj.Name : table;
            return obj with
            {
                Properties = annotations is null ? obj.Properties : Merge(obj.Properties, annotations),
                Children = obj.Children.Select(c => Overlay(c, childTable)).ToList()
            };
        }
    }

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? properties,
        IReadOnlyList<ModelPropertyAssignment> annotations)
    {
        var merged = properties is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(properties);
        foreach (var annotation in annotations)
            merged[annotation.Property] = annotation.Value;
        return merged;
    }

    private static string Key(string table, string column) => $"{table}\u001f{column}";

    // Statistics name endpoints "'Table'[Column]"; the snapshot carries "Table[Column]" or a bare
    // column name, so compare on the table and the bracketed column name alone.
    private static string RelationshipKey(string? fromTable, string? fromColumn, string? toTable, string? toColumn)
        => $"{Key(fromTable ?? "", ColumnName(fromColumn))}\u001e{Key(toTable ?? "", ColumnName(toColumn))}";

    private static string ColumnName(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return "";
        var open = reference.IndexOf('[');
        var close = reference.LastIndexOf(']');
        return open >= 0 && close > open ? reference[(open + 1)..close] : reference;
    }
}
