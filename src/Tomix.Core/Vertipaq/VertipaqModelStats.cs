namespace Tomix.Core.Vertipaq;

/// <summary>
/// VertiPaq storage statistics for a model, flattened into per-object lists. Sizes are bytes;
/// percentages are 0-100. Produced by <see cref="IVertipaqAnalyzer"/> implementations; the
/// provider maps its library types into these records so nothing provider-specific leaks out.
/// </summary>
public sealed record VertipaqModelStats(
    string ModelName,
    string? ServerName,
    DateTimeOffset? ExtractionDate,
    long TotalSize,
    int TableCount,
    int ColumnCount,
    long MaxRowCount,
    IReadOnlyList<VertipaqTableStats> Tables,
    IReadOnlyList<VertipaqColumnStats> Columns,
    IReadOnlyList<VertipaqRelationshipStats> Relationships,
    IReadOnlyList<VertipaqPartitionStats> Partitions);

public sealed record VertipaqTableStats(
    string TableName,
    long RowCount,
    long TableSize,
    long ColumnsTotalSize,
    long ColumnsDataSize,
    long ColumnsDictionarySize,
    long ColumnsHierarchiesSize,
    long RelationshipsSize,
    long UserHierarchiesSize,
    double PercentageDatabase,
    int ColumnCount,
    int PartitionCount,
    int SegmentCount,
    bool IsReferenced);

public sealed record VertipaqColumnStats(
    string TableName,
    string ColumnName,
    long Cardinality,
    string DataType,
    string Encoding,
    long TotalSize,
    long DataSize,
    long DictionarySize,
    long HierarchiesSize,
    double PercentageDatabase,
    double PercentageTable,
    double? Selectivity,
    int SegmentCount,
    int PartitionCount,
    bool IsHidden,
    bool IsReferenced,
    bool IsRowNumber,
    string State);

public sealed record VertipaqRelationshipStats(
    string RelationshipName,
    string FromTable,
    string ToTable,
    string FromColumn,
    string ToColumn,
    long UsedSize,
    long FromCardinality,
    long ToCardinality,
    long MissingKeys,
    long InvalidRows,
    double OneToManyRatio,
    bool IsActive,
    string CrossFilteringBehavior);

public sealed record VertipaqPartitionStats(
    string TableName,
    string PartitionName,
    long RowCount,
    long DataSize,
    int SegmentCount,
    string State,
    string Type,
    string Mode,
    DateTimeOffset? RefreshedTime);
