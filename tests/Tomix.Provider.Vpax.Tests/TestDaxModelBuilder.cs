using Dax.Metadata;

namespace Tomix.Provider.Vpax.Tests;

/// <summary>
/// Builds a small in-memory <see cref="Model"/> so mapper and round-trip tests run fully
/// offline: Sales (2 columns, 1 partition, 1000 rows) and Product (1 column, 100 rows),
/// related on ProductKey.
/// </summary>
internal static class TestDaxModelBuilder
{
    public static Model Build()
    {
        var model = new Model
        {
            ModelName = new DaxName("TestModel"),
            ServerName = new DaxName("test-server"),
            ExtractionDate = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)
        };

        var sales = AddTable(model, "Sales", rows: 1000);
        var salesPartition = AddPartition(sales, "Sales-Partition0");
        var amount = AddColumn(sales, salesPartition, "Amount",
            cardinality: 900, dictionarySize: 400, segmentSize: 600, segmentRows: 1000, encoding: "VALUE");
        var salesProductKey = AddColumn(sales, salesPartition, "ProductKey",
            cardinality: 100, dictionarySize: 100, segmentSize: 200, segmentRows: 1000, encoding: "HASH");

        var product = AddTable(model, "Product", rows: 100);
        var productPartition = AddPartition(product, "Product-Partition0");
        var productKey = AddColumn(product, productPartition, "ProductKey",
            cardinality: 100, dictionarySize: 50, segmentSize: 80, segmentRows: 100, encoding: "HASH");

        var relationship = new Relationship(salesProductKey, productKey)
        {
            FromCardinalityType = "Many",
            ToCardinalityType = "One",
            CrossFilteringBehavior = "OneDirection",
            IsActive = true,
            UsedSizeFrom = 24,
            MissingKeys = 2,
            InvalidRows = 1
        };
        model.Relationships.Add(relationship);

        return model;
    }

    private static Table AddTable(Model model, string name, long rows)
    {
        var table = new Table(model)
        {
            TableName = new DaxName(name),
            RowsCount = rows,
            IsReferenced = true
        };
        model.Tables.Add(table);
        return table;
    }

    private static Partition AddPartition(Table table, string name)
    {
        var partition = new Partition(table)
        {
            PartitionName = new DaxName(name),
            State = Partition.PartitionState.Read,
            Type = Partition.PartitionType.M,
            Mode = Partition.PartitionMode.Import
        };
        table.Partitions.Add(partition);
        return partition;
    }

    private static Column AddColumn(
        Table table,
        Partition partition,
        string name,
        long cardinality,
        long dictionarySize,
        long segmentSize,
        long segmentRows,
        string encoding)
    {
        var column = new Column(table)
        {
            ColumnName = new DaxName(name),
            ColumnCardinality = cardinality,
            DataType = "Int64",
            Encoding = encoding,
            DictionarySize = dictionarySize,
            State = "Ready",
            IsReferenced = true
        };
        column.ColumnSegments.Add(new Dax.Metadata.ColumnSegment(column, partition)
        {
            SegmentNumber = 0,
            SegmentRows = segmentRows,
            UsedSize = segmentSize
        });
        table.Columns.Add(column);
        return column;
    }
}
