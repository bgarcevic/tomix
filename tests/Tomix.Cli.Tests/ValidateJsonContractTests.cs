using System.Text.Json;
using Tomix.App.Validate;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the <c>tx validate --output-format json</c> contract through the real serializer and
/// the shared envelope. <c>modelName</c> rides at the top of <c>data</c> so scripts can label
/// per-model results; changes here are breaking for scripted consumers — additive only.
/// </summary>
public sealed class ValidateJsonContractTests
{
    private static ValidateModelResult SampleResult() => new(
        ModelName: "basic-tmdl",
        Valid: false,
        DurationMs: 7,
        Errors: [new ValidationIssue("DAX0001", "Table 'X' cannot be found.", "Sales[Total]", "SUM('X'[Y])")],
        Warnings: [new ValidationIssue("DAX0003", "Measure or column [Y] cannot be found.", "Sales[N]", null)]);

    [Fact]
    public void Json_PutsModelNameAtTheTopOfData()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(
            new CommandEnvelope<ValidateModelResult>(SampleResult(), []))).RootElement;

        Assert.Equal("basic-tmdl", root.GetProperty("data").GetProperty("modelName").GetString());
        Assert.False(root.GetProperty("data").GetProperty("valid").GetBoolean());
        Assert.Equal(7, root.GetProperty("data").GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public void Json_IssuesKeepTheirFieldNames()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(
            new CommandEnvelope<ValidateModelResult>(SampleResult(), []))).RootElement;

        var error = root.GetProperty("data").GetProperty("errors")[0];
        Assert.Equal("DAX0001", error.GetProperty("code").GetString());
        Assert.Equal("Table 'X' cannot be found.", error.GetProperty("message").GetString());
        Assert.Equal("Sales[Total]", error.GetProperty("objectName").GetString());
        Assert.Equal("SUM('X'[Y])", error.GetProperty("expression").GetString());
    }
}
