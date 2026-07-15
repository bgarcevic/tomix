using Dax.ViewModel;
using Tomix.Core.Vertipaq;

namespace Tomix.Provider.Vpax;

/// <summary>
/// Maps the analyzer library's view model onto the provider-agnostic
/// <see cref="VertipaqModelStats"/> records. Percentages are converted from the library's
/// 0-1 fractions to 0-100. All Dax.* types stay inside this project.
/// </summary>
internal static class VpaStatsMapper
{
    public static VertipaqModelStats Map(Dax.Metadata.Model daxModel)
    {
        var vpaModel = new VpaModel(daxModel);
        var tables = vpaModel.Tables.Select(MapTable).ToList();
        var columns = new List<VertipaqColumnStats>();
        var partitions = new List<VertipaqPartitionStats>();

        foreach (var table in vpaModel.Tables)
        {
            var hasColumns = table.ColumnsNumber > 0;
            columns.AddRange(table.Columns.Select(c => MapColumn(table.TableName, c)));

            // VpaPartition row/size/segment figures aggregate over the table's column segments
            // and throw on a column-less table, so guard those with zeros.
            partitions.AddRange(table.Partitions.Select(p => MapPartition(table.TableName, p, hasColumns)));
        }

        // Mapped from the metadata model rather than VpaRelationship: the view type only
        // exposes pre-formatted endpoint strings, and the table filter needs raw table names.
        var relationships = daxModel.Relationships.Select(MapRelationship).ToList();

        return new VertipaqModelStats(
            ModelName: daxModel.ModelName?.ToString() ?? "",
            ServerName: NullIfBlank(daxModel.ServerName?.ToString()),
            ExtractionDate: MapDate(daxModel.ExtractionDate),
            TotalSize: tables.Sum(t => t.TableSize),
            TableCount: tables.Count,
            ColumnCount: columns.Count,
            MaxRowCount: tables.Count == 0 ? 0 : tables.Max(t => t.RowCount),
            Tables: tables,
            Columns: columns,
            Relationships: relationships,
            Partitions: partitions);
    }

    private static VertipaqTableStats MapTable(VpaTable table)
        => new(
            TableName: table.TableName,
            RowCount: table.RowsCount,
            TableSize: table.TableSize,
            ColumnsTotalSize: table.ColumnsTotalSize,
            ColumnsDataSize: table.ColumnsDataSize,
            ColumnsDictionarySize: table.ColumnsDictionarySize,
            ColumnsHierarchiesSize: table.ColumnsHierarchiesSize,
            RelationshipsSize: table.RelationshipsSize,
            UserHierarchiesSize: table.UserHierarchiesSize,
            PercentageDatabase: ToPercent(table.PercentageDatabase),
            ColumnCount: table.ColumnsNumber,
            PartitionCount: table.PartitionsNumber,
            SegmentCount: table.SegmentsTotalNumber,
            IsReferenced: table.IsReferenced);

    private static VertipaqColumnStats MapColumn(string tableName, VpaColumn column)
        => new(
            TableName: tableName,
            ColumnName: column.ColumnName,
            Cardinality: column.ColumnCardinality,
            DataType: column.DataType ?? "",
            Encoding: column.Encoding ?? "",
            TotalSize: column.TotalSize,
            DataSize: column.DataSize,
            DictionarySize: column.DictionarySize,
            HierarchiesSize: column.HierarchiesSize,
            PercentageDatabase: ToPercent(column.PercentageDatabase),
            PercentageTable: ToPercent(column.PercentageTable),
            Selectivity: column.Selectivity,
            SegmentCount: column.SegmentsNumber,
            PartitionCount: column.PartitionsNumber,
            IsHidden: column.IsHidden,
            IsReferenced: column.IsReferenced,
            IsRowNumber: column.IsRowNumber,
            State: column.State ?? "");

    private static VertipaqRelationshipStats MapRelationship(Dax.Metadata.Relationship relationship)
    {
        var fromTable = relationship.FromColumn?.Table?.TableName?.Name ?? "";
        var toTable = relationship.ToColumn?.Table?.TableName?.Name ?? "";
        var fromColumn = relationship.FromColumn?.ColumnName?.Name ?? "";
        var toColumn = relationship.ToColumn?.ColumnName?.Name ?? "";
        var fromRows = relationship.FromColumn?.Table?.RowsCount ?? 0;
        var toCardinality = relationship.ToColumn?.ColumnCardinality ?? 0;

        return new VertipaqRelationshipStats(
            RelationshipName: $"'{fromTable}'[{fromColumn}] -> '{toTable}'[{toColumn}]",
            FromTable: fromTable,
            ToTable: toTable,
            FromColumn: $"'{fromTable}'[{fromColumn}]",
            ToColumn: $"'{toTable}'[{toColumn}]",
            UsedSize: relationship.UsedSize,
            FromCardinality: relationship.FromColumn?.ColumnCardinality ?? 0,
            ToCardinality: toCardinality,
            MissingKeys: relationship.MissingKeys,
            InvalidRows: relationship.InvalidRows,
            OneToManyRatio: fromRows == 0 ? 0 : (double)toCardinality / fromRows,
            IsActive: relationship.IsActive,
            CrossFilteringBehavior: relationship.CrossFilteringBehavior ?? "");
    }

    private static VertipaqPartitionStats MapPartition(string tableName, VpaPartition partition, bool hasColumns)
        => new(
            TableName: tableName,
            PartitionName: partition.PartitionName,
            RowCount: hasColumns ? partition.RowsCount : 0,
            DataSize: hasColumns ? partition.DataSize : 0,
            SegmentCount: hasColumns ? (int)partition.SegmentsNumber : 0,
            State: partition.PartitionState ?? "",
            Type: partition.PartitionType ?? "",
            Mode: partition.PartitionMode ?? "",
            RefreshedTime: MapDate(partition.RefreshedTime));

    private static double ToPercent(double fraction)
        => double.IsFinite(fraction) ? fraction * 100d : 0d;

    private static DateTimeOffset? MapDate(DateTime? value)
    {
        if (value is null || value.Value == default)
            return null;

        var date = value.Value;
        return date.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : new DateTimeOffset(date.ToUniversalTime());
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
