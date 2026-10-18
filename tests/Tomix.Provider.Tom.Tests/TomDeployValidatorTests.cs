using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Rules;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The post-deploy pass (#237) reads the object states the server reports on the deployed
/// database. TOM deserializes <c>state</c> and <c>errorMessage</c> from database JSON, so these
/// fixtures stand in for a server's answer without one.
/// </summary>
public sealed class TomDeployValidatorTests
{
    [Fact]
    public void ReadyModel_HasNoIssues()
    {
        var validation = TomDeployValidator.Inspect(Fixture(
            """{"name":"Total","expression":"1","state":"ready"}""",
            columns: """{"name":"Amount","dataType":"int64","sourceColumn":"Amount","state":"ready"}""",
            partitionState: "ready"));

        Assert.Empty(validation.Issues);
    }

    [Theory]
    [InlineData("semanticError", RuleSeverity.Error)]
    [InlineData("evaluationError", RuleSeverity.Error)]
    [InlineData("dependencyError", RuleSeverity.Error)]
    [InlineData("incomplete", RuleSeverity.Warning)]
    [InlineData("calculationNeeded", RuleSeverity.Info)]
    [InlineData("noData", RuleSeverity.Info)]
    public void MeasureState_MapsToSeverity(string state, RuleSeverity expected)
    {
        var validation = TomDeployValidator.Inspect(Fixture(
            $$"""{"name":"Total","expression":"1","state":"{{state}}","errorMessage":"server says no"}"""));

        var issue = Assert.Single(validation.Issues);
        Assert.Equal(expected, issue.Severity);
        Assert.Equal("Measure", issue.ObjectType);
        Assert.Equal("Sales[Total]", issue.ObjectName);
        Assert.Equal(char.ToUpperInvariant(state[0]) + state[1..], issue.State);
        Assert.Equal("server says no", issue.Message);
    }

    [Fact]
    public void BrokenObjects_AcrossKinds_AreAllReported()
    {
        var validation = TomDeployValidator.Inspect(Fixture(
            """{"name":"Total","expression":"[Nope]","state":"semanticError","errorMessage":"Nope not found"}""",
            columns: """
                {"name":"Amount","dataType":"int64","sourceColumn":"Amount","state":"ready"},
                {"name":"Double","type":"calculated","expression":"[Nope] * 2","state":"semanticError","errorMessage":"bad column"}
                """,
            partitionState: "noData",
            roles: """
                [{"name":"Reader","modelPermission":"read","tablePermissions":[
                  {"name":"Sales","filterExpression":"[Nope] = 1","state":"semanticError","errorMessage":"bad filter"}]}]
                """));

        Assert.Equal(3, validation.Errors);
        Assert.Equal(0, validation.Warnings);
        Assert.Equal(1, validation.Unprocessed);
        Assert.Contains(validation.Issues, i => i is { ObjectType: "CalculatedColumn", ObjectName: "Sales[Double]", Message: "bad column" });
        Assert.Contains(validation.Issues, i => i is { ObjectType: "TablePermission", ObjectName: "Reader[Sales]", Message: "bad filter" });
        Assert.Contains(validation.Issues, i => i is { ObjectType: "Partition", ObjectName: "Sales[Sales]", Severity: RuleSeverity.Info });
    }

    [Fact]
    public void Issues_ListErrorsBeforeWarningsBeforeUnprocessed()
    {
        var validation = TomDeployValidator.Inspect(Fixture(
            """
            {"name":"A","expression":"1","state":"calculationNeeded"},
            {"name":"B","expression":"1","state":"incomplete"},
            {"name":"C","expression":"1","state":"semanticError"}
            """));

        Assert.Equal(["C", "B", "A"], validation.Issues.Select(i => i.ObjectName[6..^1]));
    }

    [Fact]
    public void BlankErrorMessage_IsNull()
    {
        var validation = TomDeployValidator.Inspect(Fixture(
            """{"name":"Total","expression":"1","state":"calculationNeeded"}"""));

        Assert.Null(Assert.Single(validation.Issues).Message);
    }

    private static Database Fixture(
        string measures,
        string columns = """{"name":"Amount","dataType":"int64","sourceColumn":"Amount"}""",
        string partitionState = "ready",
        string roles = "[]")
        => JsonSerializer.DeserializeDatabase($$$"""
            {"name":"db","compatibilityLevel":1600,"model":{
              "tables":[{"name":"Sales",
                "columns":[{{{columns}}}],
                "measures":[{{{measures}}}],
                "partitions":[{"name":"Sales","state":"{{{partitionState}}}","source":{"type":"m","expression":"let a = 1 in a"}}]}],
              "roles":{{{roles}}}}}
            """);
}
