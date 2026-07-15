using Tomix.Core.Vertipaq;

namespace Tomix.Provider.Vpax.Tests;

public class VpaStatsMapperTests
{
    private readonly VertipaqModelStats _stats = VpaStatsMapper.Map(TestDaxModelBuilder.Build());

    [Fact]
    public void Maps_model_level_metadata()
    {
        Assert.Equal("TestModel", _stats.ModelName);
        Assert.Equal("test-server", _stats.ServerName);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero), _stats.ExtractionDate);
        Assert.Equal(2, _stats.TableCount);
        Assert.Equal(3, _stats.ColumnCount);
        Assert.Equal(1000, _stats.MaxRowCount);
        Assert.Equal(_stats.Tables.Sum(t => t.TableSize), _stats.TotalSize);
    }

    [Fact]
    public void Maps_table_stats()
    {
        var sales = Assert.Single(_stats.Tables, t => t.TableName == "Sales");

        Assert.Equal(1000, sales.RowCount);
        Assert.Equal(500, sales.ColumnsDictionarySize);
        Assert.Equal(800, sales.ColumnsDataSize);
        Assert.Equal(1300, sales.ColumnsTotalSize);
        Assert.True(sales.TableSize >= sales.ColumnsTotalSize);
        Assert.Equal(2, sales.ColumnCount);
        Assert.Equal(1, sales.PartitionCount);
        Assert.True(sales.IsReferenced);
        Assert.InRange(sales.PercentageDatabase, 1, 100);
    }

    [Fact]
    public void Maps_column_stats_with_percentages_scaled_to_100()
    {
        var amount = Assert.Single(_stats.Columns, c => c is { TableName: "Sales", ColumnName: "Amount" });

        Assert.Equal(900, amount.Cardinality);
        Assert.Equal("VALUE", amount.Encoding);
        Assert.Equal(400, amount.DictionarySize);
        Assert.Equal(600, amount.DataSize);
        Assert.Equal(1000, amount.TotalSize);
        Assert.Equal(1, amount.SegmentCount);

        var sales = _stats.Tables.Single(t => t.TableName == "Sales");
        Assert.Equal(100d * amount.TotalSize / sales.ColumnsTotalSize, amount.PercentageTable, precision: 6);
        Assert.Equal(100d * amount.TotalSize / _stats.TotalSize, amount.PercentageDatabase, precision: 6);
    }

    [Fact]
    public void Maps_relationship_stats()
    {
        var relationship = Assert.Single(_stats.Relationships);

        Assert.Equal("Sales", relationship.FromTable);
        Assert.Equal("Product", relationship.ToTable);
        Assert.Equal("'Sales'[ProductKey]", relationship.FromColumn);
        Assert.Equal("'Product'[ProductKey]", relationship.ToColumn);
        Assert.Equal(24, relationship.UsedSize);
        Assert.Equal(100, relationship.FromCardinality);
        Assert.Equal(100, relationship.ToCardinality);
        Assert.Equal(2, relationship.MissingKeys);
        Assert.Equal(1, relationship.InvalidRows);
        Assert.True(relationship.IsActive);
        Assert.Equal("OneDirection", relationship.CrossFilteringBehavior);
    }

    [Fact]
    public void Maps_partition_stats()
    {
        var partition = Assert.Single(_stats.Partitions, p => p.TableName == "Sales");

        Assert.Equal("Sales-Partition0", partition.PartitionName);
        Assert.Equal(1000, partition.RowCount);
        Assert.Equal(800, partition.DataSize);
        Assert.Equal(1, partition.SegmentCount);
        Assert.Equal("Read", partition.State);
        Assert.Equal("Import", partition.Mode);
    }
}
