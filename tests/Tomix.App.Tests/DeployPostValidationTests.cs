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

    // -- --rollback-on-error (ADR 0005) ----------------------------------------------------------

    [Fact]
    public async Task RolledBack_FailsWithTheBrokenObjects_AndSaysTheTargetIsUnchanged()
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", ModelDeployResult.RolledBack, 42, new DeployValidation(
            [
                Issue(RuleSeverity.Error, "Total"),
                Issue(RuleSeverity.Info, "Unprocessed")
            ])), rollbackOnError: true);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("rolled-back", result.Data!.Status);
        Assert.Equal(1, result.Data.Validation!.Errors);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_DEPLOY_ROLLED_BACK", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Rolled back the deploy to 'Prod'", diagnostic.Message);
        Assert.Contains("Sales[Total] (SemanticError)", diagnostic.Message);
        Assert.Contains("The target is unchanged.", diagnostic.Message);
        Assert.DoesNotContain("Deployed 'Prod'", diagnostic.Message);
    }

    [Fact]
    public async Task RolledBack_BecauseTheCheckCouldNotRun_SaysWhy()
    {
        var result = await Deploy(new ModelDeployResult(
            "ws", "Prod", ModelDeployResult.RolledBack, 42, Validation: null, ValidationError: "connection dropped"),
            rollbackOnError: true);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_DEPLOY_ROLLED_BACK", diagnostic.Code);
        Assert.Contains("could not be checked: connection dropped", diagnostic.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackOnError_ReachesTheProvider(bool rollbackOnError)
    {
        var session = new Session(new ModelDeployResult("ws", "Prod", "updated", 42));

        await Deploy(session, rollbackOnError);

        Assert.Equal(rollbackOnError, session.LastRequest!.RollbackOnError);
    }

    [Fact]
    public async Task RollbackOnError_WithXmla_IsAUsageError()
    {
        var session = new Session(new ModelDeployResult("ws", "Prod", "updated", 42));

        var result = await Deploy(session, rollbackOnError: true, xmla: "-");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_DEPLOY_INVALID_FLAGS", Assert.Single(result.Diagnostics).Code);
        Assert.Null(session.LastRequest);
    }

    private static Task<Tomix.Core.Results.TomixResult<DeployModelResult>> Deploy(
        ModelDeployResult deployed, bool rollbackOnError = false)
        => Deploy(new Session(deployed), rollbackOnError);

    private static async Task<Tomix.Core.Results.TomixResult<DeployModelResult>> Deploy(
        Session session, bool rollbackOnError, string? xmla = null)
    {
        using var config = new TempConfigDir();
        var handler = new DeployModelHandler([new Provider(session)], config.State);
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
                XmlaOutput: xmla,
                Ci: null,
                RollbackOnError: rollbackOnError),
            CancellationToken.None);
    }

    private sealed class Provider(Session session) : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class Session(ModelDeployResult deployed) : IModelSession, IModelDeploySession
    {
        public ModelDeployRequest? LastRequest { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(deployed);
        }

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
