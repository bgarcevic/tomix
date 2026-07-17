using System.Text.Json;
using Tomix.App.Query;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the <c>tx query --output-format json</c> contract (field names and cell-value
/// serialization) and the deterministic CSV cell rendering. Changes here are breaking
/// for scripted consumers — additive only.
/// </summary>
public sealed class QueryResultJsonContractTests
{
    private static QueryModelResult SampleResult() => new(
        Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
        Database: "MyModel",
        Columns:
        [
            new QueryColumn("Sales[Amount]", "decimal"),
            new QueryColumn("[Is Active]", "boolean"),
            new QueryColumn("Sales[Date]", "dateTime")
        ],
        Rows:
        [
            [100.5m, true, new DateTime(2026, 7, 15, 13, 30, 0)],
            [null, false, null]
        ],
        RowCount: 2,
        Truncated: true,
        DurationMs: 231);

    [Fact]
    public void Json_UsesDocumentedFieldNames()
    {
        var json = JsonDocument.Parse(JsonOutput.Serialize(SampleResult()));
        var root = json.RootElement;

        Assert.Equal("MyModel", root.GetProperty("database").GetString());
        Assert.StartsWith("powerbi://", root.GetProperty("server").GetString());
        Assert.Equal(2, root.GetProperty("rowCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(231, root.GetProperty("durationMs").GetInt64());

        var column = root.GetProperty("columns")[0];
        Assert.Equal("Sales[Amount]", column.GetProperty("name").GetString());
        Assert.Equal("decimal", column.GetProperty("type").GetString());
    }

    [Fact]
    public void Json_SerializesCellPrimitives()
    {
        var json = JsonDocument.Parse(JsonOutput.Serialize(SampleResult()));
        var rows = json.RootElement.GetProperty("rows");

        Assert.Equal(100.5m, rows[0][0].GetDecimal());
        Assert.True(rows[0][1].GetBoolean());
        Assert.StartsWith("2026-07-15T13:30:00", rows[0][2].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1][0].ValueKind);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(true, "True")]
    [InlineData(1234L, "1234")]
    public void FormatCell_IsInvariantAndDeterministic(object? value, string expected)
        => Assert.Equal(expected, QueryResultRenderer.FormatCell(value));

    [Fact]
    public void FormatCell_DateTime_IsIso8601()
        => Assert.Equal(
            "2026-07-15T13:30:00",
            QueryResultRenderer.FormatCell(new DateTime(2026, 7, 15, 13, 30, 0)));

    [Fact]
    public void Json_PerfSections_AreNull_WhenAbsent()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(SampleResult())).RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("timings").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("plans").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("benchmark").ValueKind);
    }

    [Fact]
    public void Json_PerfSections_UseDocumentedFieldNames_WhenPresent()
    {
        var result = SampleResult() with
        {
            Timings = new QueryTimings(231, 300, 40, 191, 250, 3, 1),
            Plans = [new QueryPlan("logical", "tree"), new QueryPlan("physical", "tree2")],
            Benchmark = new QueryBenchmark(
                [new QueryBenchmarkRun(1, Cold: true, TotalMs: 231, SeMs: 191)],
                new QueryStat(231, 231, 231, 0),
                new QueryStat(191, 191, 191, 0))
        };
        var root = JsonDocument.Parse(JsonOutput.Serialize(result)).RootElement;

        var timings = root.GetProperty("timings");
        Assert.Equal(231, timings.GetProperty("totalMs").GetInt64());
        Assert.Equal(40, timings.GetProperty("formulaEngineMs").GetInt64());
        Assert.Equal(191, timings.GetProperty("storageEngineMs").GetInt64());
        Assert.Equal(3, timings.GetProperty("storageEngineQueryCount").GetInt32());
        Assert.Equal(1, timings.GetProperty("storageEngineCacheHits").GetInt32());

        var plans = root.GetProperty("plans");
        Assert.Equal("logical", plans[0].GetProperty("kind").GetString());
        Assert.Equal("tree", plans[0].GetProperty("text").GetString());

        var benchmark = root.GetProperty("benchmark");
        Assert.Equal(1, benchmark.GetProperty("runs").GetArrayLength());
        Assert.True(benchmark.GetProperty("runs")[0].GetProperty("cold").GetBoolean());
        Assert.Equal(231, benchmark.GetProperty("totalStats").GetProperty("avg").GetDouble());
        Assert.Equal(191, benchmark.GetProperty("seStats").GetProperty("avg").GetDouble());
    }
}
