using System.Globalization;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.App.Vertipaq;

/// <summary>One mutation target: the object path plus its <c>Annotation:Vertipaq_*</c> assignments.</summary>
public sealed record VertipaqAnnotationTarget(
    string Path,
    ModelObjectKind? Type,
    IReadOnlyList<ModelPropertyAssignment> Assignments);

/// <summary>
/// Maps statistics to <c>Vertipaq_*</c> annotation assignments (the community naming convention
/// consumed by the bundled BPA rules: <c>Vertipaq_RowCount</c>, <c>Vertipaq_Cardinality</c>,
/// <c>Vertipaq_RIViolationInvalidRows</c>). Values are invariant-culture raw numbers; the
/// extraction date is ISO 8601 UTC. Row-number columns are skipped — they are storage artifacts
/// that cannot be addressed as model objects.
/// </summary>
public static class VertipaqAnnotationBuilder
{
    public static IReadOnlyList<VertipaqAnnotationTarget> Build(VertipaqModelStats stats)
    {
        var targets = new List<VertipaqAnnotationTarget> { new(".", null, ForModel(stats)) };

        foreach (var table in stats.Tables)
            targets.Add(new VertipaqAnnotationTarget(Segment(table.TableName), ModelObjectKind.Table, ForTable(table)));

        foreach (var column in stats.Columns)
        {
            if (column.IsRowNumber)
                continue;

            targets.Add(new VertipaqAnnotationTarget(
                $"{Segment(column.TableName)}/{Segment(column.ColumnName)}",
                ModelObjectKind.Column,
                ForColumn(column)));
        }

        foreach (var relationship in stats.Relationships)
        {
            targets.Add(new VertipaqAnnotationTarget(
                $"{relationship.FromColumn}->{relationship.ToColumn}",
                ModelObjectKind.Relationship,
                ForRelationship(relationship)));
        }

        return targets;
    }

    /// <summary>The model-level statistics annotations.</summary>
    internal static IReadOnlyList<ModelPropertyAssignment> ForModel(VertipaqModelStats stats) =>
    [
        Annotation("Vertipaq_TotalSize", stats.TotalSize),
        Annotation("Vertipaq_TableCount", stats.TableCount),
        Annotation("Vertipaq_ColumnCount", stats.ColumnCount),
        new ModelPropertyAssignment(
            "Annotation:Vertipaq_ExtractionDate",
            (stats.ExtractionDate ?? DateTimeOffset.UtcNow).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
    ];

    internal static IReadOnlyList<ModelPropertyAssignment> ForTable(VertipaqTableStats table) =>
    [
        Annotation("Vertipaq_RowCount", table.RowCount),
        Annotation("Vertipaq_TableSize", table.TableSize),
        Annotation("Vertipaq_ColumnsTotalSize", table.ColumnsTotalSize),
        Annotation("Vertipaq_RelationshipsSize", table.RelationshipsSize),
        Annotation("Vertipaq_UserHierarchiesSize", table.UserHierarchiesSize)
    ];

    internal static IReadOnlyList<ModelPropertyAssignment> ForColumn(VertipaqColumnStats column) =>
    [
        Annotation("Vertipaq_Cardinality", column.Cardinality),
        Annotation("Vertipaq_TotalSize", column.TotalSize),
        Annotation("Vertipaq_DataSize", column.DataSize),
        Annotation("Vertipaq_DictionarySize", column.DictionarySize),
        Annotation("Vertipaq_HierarchiesSize", column.HierarchiesSize),
        new ModelPropertyAssignment("Annotation:Vertipaq_Encoding", column.Encoding)
    ];

    internal static IReadOnlyList<ModelPropertyAssignment> ForRelationship(VertipaqRelationshipStats relationship) =>
    [
        Annotation("Vertipaq_RIViolationInvalidRows", relationship.InvalidRows),
        Annotation("Vertipaq_MissingKeys", relationship.MissingKeys),
        Annotation("Vertipaq_UsedSize", relationship.UsedSize)
    ];

    private static ModelPropertyAssignment Annotation(string name, long value)
        => new($"Annotation:{name}", value.ToString(CultureInfo.InvariantCulture));

    // Quoting forces a literal match in ObjectPath.Parse, so always quote: unquoted names that
    // collide with container keywords (Measures, Columns, ...) would be consumed as keywords,
    // and a leading apostrophe would open a quoted group. Embedded quotes double per the parser.
    private static string Segment(string name)
        => $"'{name.Replace("'", "''", StringComparison.Ordinal)}'";
}
