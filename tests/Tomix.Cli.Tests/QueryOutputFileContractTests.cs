using System.Text.Json.Nodes;
using Tomix.App.Query;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the <c>query --output-file</c> boundary: the file is data for jq/pandas, so it carries the
/// bare result and never the stdout envelope.
/// </summary>
/// <remarks>
/// docs/cli-ux-guidelines.md and <c>CommandEnvelope&lt;T&gt;</c> both listed this path as outside
/// the envelope and both claimed a test pinned it. Neither was true — the envelope work covered
/// csv and the model-shaped <c>get</c> formats only — so the one boundary a strict reader would
/// break on had nothing holding it.
/// </remarks>
public sealed class QueryOutputFileContractTests
{
    private static QueryModelResult SampleResult() => new(
        Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
        Database: "MyModel",
        Columns: [new QueryColumn("Sales[Amount]", "decimal")],
        Rows: [[100.5m]],
        RowCount: 1,
        Truncated: false,
        DurationMs: 12);

    [Fact]
    public void JsonFile_IsNotEnveloped()
    {
        using var dir = new TempDir("tomix-query-file");
        var path = dir.Combine("out.json");

        QueryResultRenderer.WriteFile(SampleResult(), path, OutputFormats.Json);

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(root.ContainsKey("data"));
        Assert.False(root.ContainsKey("diagnostics"));
        // The result's own fields sit at the top level, where a strict reader expects them.
        Assert.Equal("MyModel", root["database"]!.GetValue<string>());
        Assert.Equal(1, root["rowCount"]!.GetValue<int>());
    }

    [Fact]
    public void CsvFile_IsNotEnveloped()
    {
        using var dir = new TempDir("tomix-query-file");
        var path = dir.Combine("out.csv");

        QueryResultRenderer.WriteFile(SampleResult(), path, OutputFormats.Csv);

        // Header then one row, and nothing above them: asserting the exact line set is what makes
        // this falsifiable. A "does not contain 'diagnostics'" check would pass no matter what the
        // writer did, since the sample carries no such text to begin with.
        Assert.Equal(["Sales[Amount]", "100.5"], File.ReadAllLines(path));
    }
}
