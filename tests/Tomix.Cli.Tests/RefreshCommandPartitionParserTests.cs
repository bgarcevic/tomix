using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

public class RefreshCommandPartitionParserTests
{
    [Fact]
    public void ParsePartitions_ReturnsEmpty_When_NoValues()
    {
        var result = RefreshCommand.ParsePartitions(null, out var badValue);

        Assert.NotNull(result);
        Assert.Empty(result!);
        Assert.Null(badValue);
    }

    [Fact]
    public void ParsePartitions_ParsesTableDotPartition()
    {
        var result = RefreshCommand.ParsePartitions(["Sales.Internet", "Inventory.Q1"], out var badValue);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal("Sales", result[0].Table);
        Assert.Equal("Internet", result[0].Partition);
        Assert.Equal("Inventory", result[1].Table);
        Assert.Equal("Q1", result[1].Partition);
        Assert.Null(badValue);
    }

    [Theory]
    [InlineData("no-dot")]        // missing separator
    [InlineData(".LeadingDot")]   // empty table
    [InlineData("TrailingDot.")]  // empty partition
    public void ParsePartitions_ReportsBadValue_InsteadOf_WritingToConsole(string value)
    {
        var result = RefreshCommand.ParsePartitions([value], out var badValue);

        Assert.Null(result);
        Assert.Equal(value, badValue);
    }

    [Fact]
    public void ParsePartitions_ReportsFirstBadValue()
    {
        var result = RefreshCommand.ParsePartitions(["Sales.Internet", "bogus"], out var badValue);

        Assert.Null(result);
        Assert.Equal("bogus", badValue);
    }
}
