using System.Text.Json;
using Tomix.App.Deploy;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the post-deploy <c>validation</c> object (#237) in <c>tx deploy --output-format json</c>.
/// It is additive: absent on previews, scripts and providers that do not inspect, so the
/// existing deploy JSON is unchanged; present, it carries counts and one entry per object.
/// </summary>
public sealed class DeployJsonContractTests
{
    [Fact]
    public void Validation_CarriesCountsAndIssues()
    {
        var data = Serialize(new DeployModelResult("ws", "Prod", "updated", 42, null, null)
        {
            Validation = new DeployValidation(
            [
                new DeployedObjectIssue(RuleSeverity.Error, "Measure", "Sales[Total]", "SemanticError", "bad"),
                new DeployedObjectIssue(RuleSeverity.Info, "Partition", "Sales[Fact]", "NoData", null)
            ])
        });

        var validation = data.GetProperty("validation");
        Assert.Equal(1, validation.GetProperty("errors").GetInt32());
        Assert.Equal(0, validation.GetProperty("warnings").GetInt32());
        Assert.Equal(1, validation.GetProperty("unprocessed").GetInt32());

        var issue = validation.GetProperty("issues")[0];
        Assert.Equal("Error", issue.GetProperty("severity").GetString());
        Assert.Equal("Measure", issue.GetProperty("objectType").GetString());
        Assert.Equal("Sales[Total]", issue.GetProperty("objectName").GetString());
        Assert.Equal("SemanticError", issue.GetProperty("state").GetString());
        Assert.Equal("bad", issue.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, validation.GetProperty("issues")[1].GetProperty("message").ValueKind);
    }

    [Fact]
    public void Validation_IsOmittedWhenAbsent()
    {
        var data = Serialize(new DeployModelResult("ws", "Prod", "updated", 42, null, null));

        Assert.False(data.TryGetProperty("validation", out _));
        Assert.Equal(
            ["server", "database", "status", "durationMs", "diff"],
            data.EnumerateObject().Select(p => p.Name));
    }

    /// <summary><c>--rollback-on-error</c> (ADR 0005) adds one status value and no new fields: the
    /// validation that caused the rollback rides along as for any deploy.</summary>
    [Fact]
    public void RolledBack_IsAStatus_WithTheValidationThatCausedIt()
    {
        var data = Serialize(new DeployModelResult("ws", "Prod", ModelDeployResult.RolledBack, 42, null, null)
        {
            Validation = new DeployValidation(
                [new DeployedObjectIssue(RuleSeverity.Error, "Measure", "Sales[Total]", "SemanticError", "bad")])
        });

        Assert.Equal("rolled-back", data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("validation").GetProperty("errors").GetInt32());
        Assert.Equal(
            ["server", "database", "status", "durationMs", "diff", "validation"],
            data.EnumerateObject().Select(p => p.Name));
    }

    private static JsonElement Serialize(DeployModelResult result)
        => JsonDocument.Parse(JsonOutput.Serialize(new CommandEnvelope<DeployModelResult>(result, [])))
            .RootElement.GetProperty("data");
}
