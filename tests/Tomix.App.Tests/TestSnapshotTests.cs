using System.Text;
using Tomix.App.Test;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class TestSnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-snapshot-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SnapshotPath(string name = "test") => Path.Combine(_dir, $"{name}.expected.json");

    private static ModelQueryResult Result() => new(
        "s",
        "d",
        [new QueryColumn("Sales[Region]", "string"), new QueryColumn("[Total]", "decimal")],
        [["East", 1234.50m], ["West", null]],
        Truncated: false,
        DurationMs: 0);

    [Fact]
    public void FromResult_FormatsCellsToCanonicalStrings()
    {
        var snapshot = TestSnapshotFile.FromResult(Result(), "abc");

        Assert.Equal(TestSnapshotFile.CurrentVersion, snapshot.Version);
        Assert.Equal("abc", snapshot.QuerySha256);
        Assert.Equal(["East", "1234.50"], snapshot.Rows[0]);
        Assert.Equal("West", snapshot.Rows[1][0]);
        Assert.Null(snapshot.Rows[1][1]);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = SnapshotPath();
        var snapshot = TestSnapshotFile.FromResult(Result(), "abc");

        Assert.True(TestSnapshotFile.Save(path, snapshot));
        var loaded = TestSnapshotFile.Load(path, out var error);

        Assert.Null(error);
        Assert.Equal(snapshot, loaded, (a, b) =>
            a!.Version == b!.Version
            && a.QuerySha256 == b.QuerySha256
            && a.Columns.SequenceEqual(b.Columns)
            && a.Rows.Count == b.Rows.Count
            && a.Rows.Zip(b.Rows).All(p => p.First.SequenceEqual(p.Second)));
    }

    [Fact]
    public void Save_SkipsWrite_WhenContentIsIdentical()
    {
        var path = SnapshotPath();
        var snapshot = TestSnapshotFile.FromResult(Result(), "abc");

        Assert.True(TestSnapshotFile.Save(path, snapshot));
        Assert.False(TestSnapshotFile.Save(path, snapshot));
    }

    [Fact]
    public void Save_IsByteDeterministic_AndBomLess()
    {
        var snapshot = TestSnapshotFile.FromResult(Result(), "abc");
        TestSnapshotFile.Save(SnapshotPath("a"), snapshot);
        TestSnapshotFile.Save(SnapshotPath("b"), snapshot);

        var bytes = File.ReadAllBytes(SnapshotPath("a"));
        Assert.Equal(bytes, File.ReadAllBytes(SnapshotPath("b")));
        Assert.NotEqual(0xEF, bytes[0]);                       // no UTF-8 BOM
        Assert.Equal((byte)'\n', bytes[^1]);                   // trailing newline
        Assert.DoesNotContain((byte)'\r', bytes);              // "\n" line endings on every OS
    }

    [Fact]
    public void Serialize_UsesCamelCaseContract()
    {
        var json = TestSnapshotFile.Serialize(TestSnapshotFile.FromResult(Result(), "abc"));

        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"querySha256\": \"abc\"", json);
        Assert.Contains("\"columns\":", json);
        Assert.Contains("\"rows\":", json);
        Assert.Contains("\"name\": \"Sales[Region]\"", json);
        Assert.Contains("\"type\": \"string\"", json);
    }

    [Fact]
    public void Load_ReportsError_ForMalformedJson()
    {
        var path = SnapshotPath();
        File.WriteAllText(path, "{ not json", new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void Load_ReportsError_ForUnsupportedVersion()
    {
        var path = SnapshotPath();
        var future = TestSnapshotFile.FromResult(Result(), "abc") with { Version = 99 };
        File.WriteAllText(path, TestSnapshotFile.Serialize(future), new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.Contains("version 99", error);
    }

    [Fact]
    public void Load_ReportsError_ForEmptyObject()
    {
        var path = SnapshotPath();
        File.WriteAllText(path, "{}", new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Load_ReportsError_WhenRowHasWrongCellCount()
    {
        var path = SnapshotPath();
        File.WriteAllText(path, """
            { "version": 1, "querySha256": "abc",
              "columns": [{ "name": "A", "type": "string" }, { "name": "B", "type": "int64" }],
              "rows": [["ok", "1"], ["short"]] }
            """, new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.Contains("row 2 has 1 cell(s), expected 2", error);
    }

    [Fact]
    public void Load_ReportsError_WhenRowIsNull()
    {
        var path = SnapshotPath();
        File.WriteAllText(path, """
            { "version": 1, "querySha256": "abc",
              "columns": [{ "name": "A", "type": "string" }],
              "rows": [null] }
            """, new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.Contains("row 1", error);
    }

    [Fact]
    public void Load_ReportsError_WhenColumnIsIncomplete()
    {
        var path = SnapshotPath();
        File.WriteAllText(path, """
            { "version": 1, "querySha256": "abc",
              "columns": [{ "name": "A" }],
              "rows": [] }
            """, new UTF8Encoding(false));

        Assert.Null(TestSnapshotFile.Load(path, out var error));
        Assert.Contains("column", error);
    }

    [Theory]
    [InlineData("EVALUATE 'Sales'", "EVALUATE 'Sales'")]
    [InlineData("EVALUATE 'Sales'\r\n", "EVALUATE 'Sales'\n")]
    [InlineData("  EVALUATE 'Sales'  ", "EVALUATE 'Sales'")]
    public void ComputeQueryHash_NormalizesLineEndingsAndOuterWhitespace(string a, string b)
        => Assert.Equal(TestSnapshotFile.ComputeQueryHash(a), TestSnapshotFile.ComputeQueryHash(b));

    [Fact]
    public void ComputeQueryHash_DiffersForDifferentQueries()
        => Assert.NotEqual(
            TestSnapshotFile.ComputeQueryHash("EVALUATE 'Sales'"),
            TestSnapshotFile.ComputeQueryHash("EVALUATE 'Costs'"));
}
