using Tomix.App.Deploy;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.App.Tests;

/// <summary>
/// The post-deploy pass (#237) and its exit-code contract: an object the server reports in an
/// error state fails the deploy (exit 1) even though the model is deployed; incomplete objects
/// and an inspection that could not run are warnings (exit 0); unprocessed objects are only
/// reported. The result keeps its data in every case, so JSON shows what was deployed.
/// </summary>
public sealed class DeployPostValidationTests
{
    [Theory]
    [InlineData(RuleSeverity.Error, 1)]
    [InlineData(RuleSeverity.Warning, 0)]
    [InlineData(RuleSeverity.Info, 0)]
    public async Task ExitCode_FollowsTheWorstSeverity(RuleSeverity severity, int expectedExit)
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", "updated", 42, new DeployValidation([Issue(severity, "Total")])));

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(expectedExit == 0, result.Success);
        Assert.Equal("updated", result.Data!.Status);
        Assert.Single(result.Data.Validation!.Issues);
    }

    [Fact]
    public async Task ErrorState_FailsWithANamedDiagnostic_AndKeepsTheData()
    {
        var result = await Deploy(new ModelDeployResult("ws", "Prod", "updated", 42, new DeployValidation(
        [
            Issue(RuleSeverity.Error, "Total"),
            Issue(RuleSeverity.Info, "Unprocessed")
        ])));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_DEPLOY_VALIDATION_FAILED", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Deployed 'Prod'", diagnostic.Message);
        Assert.Contains("1 object(s) in an error state: Sales[Total] (SemanticError)", diagnostic.Message);
        Assert.DoesNotContain("Unprocessed", diagnostic.Message);
        Assert.Equal(1, result.Data!.Validation!.Errors);
        Assert.Equal(1, result.Data.Validation.Unprocessed);
    }

    [Fact]
    public async Task IncompleteObjects_WarnAndSucceed()
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", "updated", 42, new DeployValidation([Issue(RuleSeverity.Warning, "Half")])));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_DEPLOY_VALIDATION_WARNINGS", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Sales[Half]", diagnostic.Message);
    }

    [Fact]
    public async Task UnprocessedObjectsOnly_AreNotDiagnostics()
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", "created", 42, new DeployValidation([Issue(RuleSeverity.Info, "Fact")])));

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task InspectionFailure_WarnsButTheDeploySucceeds()
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", "updated", 42, Validation: null, ValidationError: "connection dropped"));

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Data!.Validation);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_DEPLOY_VALIDATION_UNAVAILABLE", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("connection dropped", diagnostic.Message);
    }

    [Fact]
    public async Task ProviderWithoutInspection_ReportsNothing()
    {
        var result = await Deploy(new ModelDeployResult("ws", "Prod", "updated", 42));

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Data!.Validation);
    }

    [Fact]
    public void Message_NamesTheFirstObjects_AndCountsTheRest()
    {
        var issues = Enumerable.Range(1, DeployOperation.NamedObjectLimit + 2)
            .Select(i => Issue(RuleSeverity.Error, $"M{i}"))
            .ToList();

        var (diagnostics, failed) = DeployOperation.PostDeployDiagnostics("Prod", new DeployValidation(issues), null);

        Assert.True(failed);
        var message = Assert.Single(diagnostics).Message;
        Assert.Contains($"Sales[M{DeployOperation.NamedObjectLimit}]", message);
        Assert.DoesNotContain($"Sales[M{DeployOperation.NamedObjectLimit + 1}]", message);
        Assert.EndsWith("and 2 more.", message);
    }

    private static DeployedObjectIssue Issue(RuleSeverity severity, string name)
        => new(severity, "Measure", $"Sales[{name}]", severity switch
        {
            RuleSeverity.Error => "SemanticError",
            RuleSeverity.Warning => "Incomplete",
            _ => "CalculationNeeded"
        }, Message: null);

    private static async Task<Tomix.Core.Results.TomixResult<DeployModelResult>> Deploy(ModelDeployResult deployed)
    {
        using var config = new TempConfigDir();
        var handler = new DeployModelHandler([new Provider(deployed)], config.State);
        return await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("local-model"),
                Server: "ws",
                Database: "Prod",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Ci: null),
            CancellationToken.None);
    }

    private sealed class Provider(ModelDeployResult deployed) : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new Session(deployed));
    }

    private sealed class Session(ModelDeployResult deployed) : IModelSession, IModelDeploySession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(deployed);

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
