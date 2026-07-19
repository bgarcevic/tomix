using Tomix.App.Test;

namespace Tomix.App.Tests;

public sealed class TestDiscoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-discovery-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string relativePath, string content = "EVALUATE 'Sales'")
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Discover_SingleFile_YieldsOneTest()
    {
        var daxPath = WriteFile("sales-by-region.dax");

        var test = Assert.Single(TestDiscovery.Discover(daxPath));
        Assert.Equal("sales-by-region", test.Name);
        Assert.Equal(daxPath, test.DaxPath);
        Assert.Equal(Path.Combine(_dir, "sales-by-region.expected.json"), test.ExpectedPath);
    }

    [Fact]
    public void Discover_Directory_IsRecursiveAndOrdinalSorted()
    {
        WriteFile("totals/z-last.dax");
        WriteFile("totals/a-first.dax");
        WriteFile("b-root.dax");

        var tests = TestDiscovery.Discover(_dir);

        Assert.Equal(["b-root", "totals/a-first", "totals/z-last"], tests.Select(t => t.Name));
    }

    [Fact]
    public void Discover_PairsExpectedSnapshotNextToDaxFile()
    {
        WriteFile("totals/sales.dax");

        var test = Assert.Single(TestDiscovery.Discover(_dir));
        Assert.Equal(Path.Combine(_dir, "totals", "sales.expected.json"), test.ExpectedPath);
    }

    [Fact]
    public void Discover_IgnoresNonDaxFiles()
    {
        WriteFile("sales.dax");
        WriteFile("sales.expected.json", "{}");
        WriteFile("readme.md", "# docs");

        var test = Assert.Single(TestDiscovery.Discover(_dir));
        Assert.Equal("sales", test.Name);
    }

    [Theory]
    [InlineData("totals/sales", null, true)]
    [InlineData("totals/sales", "", true)]
    [InlineData("totals/sales", "totals/sales", true)]
    [InlineData("totals/sales", "TOTALS/SALES", true)]
    [InlineData("totals/sales", "totals/*", true)]
    [InlineData("totals/sales", "*sales*", true)]
    [InlineData("totals/sales", "sales", false)]
    [InlineData("totals/sales", "totals/sale?", true)]
    [InlineData("totals/sales", "other/*", false)]
    public void MatchesFilter_SupportsWildcards(string name, string? pattern, bool expected)
        => Assert.Equal(expected, TestDiscovery.MatchesFilter(name, pattern));
}
