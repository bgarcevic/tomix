using System.Text.Json;
using Tomix.App.Test;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the <c>tx test --output-format json</c> contract: camelCase field names, enum
/// outcomes as strings, and the difference shape. Changes here are breaking for scripted
/// consumers — additive only.
/// </summary>
public sealed class TestRunJsonContractTests
{
    private static TestRunResult SampleResult() => new(
        Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
        Database: "MyModel",
        Path: "./tests",
        Tests:
        [
            new TestCaseResult("totals/sales", "/tests/totals/sales.dax", TestOutcome.Passed, DurationMs: 12),
            new TestCaseResult(
                "totals/costs",
                "/tests/totals/costs.dax",
                TestOutcome.Failed,
                DurationMs: 15,
                Message: "2 difference(s).",
                Differences:
                [
                    new TestDifference("cell", Row: 1, Column: "[Total]", Expected: "1", Actual: "2"),
                    new TestDifference("rowCount", Row: null, Column: null, Expected: "2 row(s)", Actual: "3 row(s)")
                ],
                TotalDifferences: 2)
        ],
        Passed: 1,
        Failed: 1,
        Missing: 0,
        Errored: 0,
        Updated: 0,
        DurationMs: 27);

    [Fact]
    public void Json_UsesDocumentedFieldNames()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(SampleResult())).RootElement;

        Assert.StartsWith("powerbi://", root.GetProperty("server").GetString());
        Assert.Equal("MyModel", root.GetProperty("database").GetString());
        Assert.Equal("./tests", root.GetProperty("path").GetString());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("missing").GetInt32());
        Assert.Equal(0, root.GetProperty("errored").GetInt32());
        Assert.Equal(0, root.GetProperty("updated").GetInt32());
        Assert.Equal(27, root.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public void Json_SerializesOutcomesAsStrings()
    {
        var tests = JsonDocument.Parse(JsonOutput.Serialize(SampleResult())).RootElement.GetProperty("tests");

        Assert.Equal("Passed", tests[0].GetProperty("outcome").GetString());
        Assert.Equal("Failed", tests[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public void Json_TestCase_UsesDocumentedFieldNames()
    {
        var test = JsonDocument.Parse(JsonOutput.Serialize(SampleResult())).RootElement.GetProperty("tests")[1];

        Assert.Equal("totals/costs", test.GetProperty("name").GetString());
        Assert.Equal("/tests/totals/costs.dax", test.GetProperty("file").GetString());
        Assert.Equal(15, test.GetProperty("durationMs").GetInt64());
        Assert.Equal("2 difference(s).", test.GetProperty("message").GetString());
        Assert.Equal(2, test.GetProperty("totalDifferences").GetInt32());

        var difference = test.GetProperty("differences")[0];
        Assert.Equal("cell", difference.GetProperty("kind").GetString());
        Assert.Equal(1, difference.GetProperty("row").GetInt32());
        Assert.Equal("[Total]", difference.GetProperty("column").GetString());
        Assert.Equal("1", difference.GetProperty("expected").GetString());
        Assert.Equal("2", difference.GetProperty("actual").GetString());
    }

    [Fact]
    public void Json_PassedTest_HasNullMessageAndDifferences()
    {
        var test = JsonDocument.Parse(JsonOutput.Serialize(SampleResult())).RootElement.GetProperty("tests")[0];

        Assert.Equal(JsonValueKind.Null, test.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.GetProperty("differences").ValueKind);
    }
}
