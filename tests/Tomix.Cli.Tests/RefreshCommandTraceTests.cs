using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

public class RefreshCommandTraceTests
{
    [Theory]
    [InlineData(null, "-")]      // bare --trace (ZeroOrOne GetValue returns null) -> stderr
    [InlineData("", "-")]        // empty value -> stderr
    [InlineData("-", "-")]       // explicit "-" -> stderr
    [InlineData("trace.log", "trace.log")]   // file path -> file
    [InlineData("C:/tmp/x.log", "C:/tmp/x.log")] // absolute path preserved
    public void ResolveTracePath_Normalizes_Bare_And_Keeps_Paths(string? input, string expected)
        => Assert.Equal(expected, RefreshCommand.ResolveTracePath(input));

    [Fact]
    public void OpenTraceWriter_Returns_Null_When_Path_Null()
        => Assert.Null(RefreshCommand.OpenTraceWriter(tracePath: null, quiet: false));

    [Fact]
    public void OpenTraceWriter_Returns_ConsoleError_For_Stderr_Path()
    {
        // "-" is the canonical "stderr" sentinel. The writer is wrapped so that disposing it
        // (e.g. via `using`) does not dispose the process-shared Console.Error.
        using (var writer = RefreshCommand.OpenTraceWriter("-", quiet: false))
        {
            var wrapper = Assert.IsType<NonDisposingTextWriter>(writer);
            Assert.Same(Console.Error, wrapper.Inner);
        }

        // Console.Error must still be usable after the wrapper's `using` scope ends.
        Console.Error.WriteLine("probe: stderr survived trace-writer disposal");
    }

    [Fact]
    public void OpenTraceWriter_Returns_NullWriter_When_Stderr_And_Quiet()
    {
        // --quiet + --trace should still parse but suppress stderr output to avoid interleaving.
        using var writer = RefreshCommand.OpenTraceWriter("-", quiet: true);
        Assert.Same(TextWriter.Null, writer);
    }

    [Fact]
    public void OpenTraceWriter_Opens_File_For_Path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tomix-trace-{Guid.NewGuid():N}.log");
        try
        {
            // Dispose before reading so the file handle is released (AutoFlush alone doesn't
            // close the stream; the OS still sees it as in use until Dispose).
            using (var writer = RefreshCommand.OpenTraceWriter(path, quiet: false))
            {
                Assert.NotNull(writer);
                writer!.WriteLine("probe");
            }

            Assert.Contains("probe", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

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
