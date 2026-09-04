using Tomix.App.Deploy;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class DeployModelHandlerTests
{

    private static Tomix.App.State.CliStateStore TestState => new(
        Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"));
    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoModelSpecified()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference(""),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_MODEL", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    /// <summary>
    /// A local active connection with a remote workspace-mode mirror (the edit-locally,
    /// deploy-to-workspace workflow) must resolve the mirror as the deploy target instead of
    /// failing with TOMIX_DEPLOY_NO_TARGET, matching refresh's target resolution.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LocalSessionWithRemoteMirror_TargetsMirror()
    {
        var session = new Tomix.App.State.CliConnectionState(
            Server: null,
            Database: "Bosteder",
            Model: "samples/basic-tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/sandbox");

        var handler = new DeployModelHandler([new StubDeployProvider()], TestState, () => session);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: null,
                Database: null,
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/sandbox", result.Data!.Server);
        Assert.Equal("Bosteder", result.Data.Database);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenRoleMembersWithoutRoles()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null,
                DeployOptions: new ModelDeployOptions(DeployRoleMembers: true)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DEPLOY_INVALID_FLAGS", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenPolicyPartitionsWithoutPartitions()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null,
                DeployOptions: new ModelDeployOptions(DeployPolicyPartitions: true)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DEPLOY_INVALID_FLAGS", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_FullOptions_PassValidation()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null,
                DeployOptions: ModelDeployOptions.Full),
            CancellationToken.None);

        Assert.True(result.Success);
    }

    /// <summary>
    /// The dry-run diff answers "what will this deploy change on the target": an object that
    /// exists in the source but not on the target must read as "added" (it will be added to
    /// the target), not "removed".
    /// </summary>
    [Fact]
    public async Task HandleAsync_DryRun_DiffsInDeployDirection()
    {
        var planned = SnapshotWithMeasure(includeMeasure: true);
        var target = SnapshotWithMeasure(includeMeasure: false);
        var provider = new DirectionalDeployProvider(
            reference => reference.Value == "local-model" ? planned : target,
            () => new ModelDeployPlan(TargetExists: true, target, planned));

        var handler = new DeployModelHandler([provider], TestState);
        var result = await handler.HandleAsync(DryRunRequest(), CancellationToken.None);

        Assert.True(result.Success);
        var change = Assert.Single(result.Data!.Diff!.Changes);
        Assert.Equal("added", change.Action);
        Assert.Equal("Sales/Total Sales", change.Path);
    }

    /// <summary>
    /// The proof of the #128 fix at the handler level: the dry run diffs the deploy plan, not
    /// the raw source. Here the source carries a measure the deploy will not send (the plan
    /// preserves the target's state), so the honest answer is "no changes" — diffing the raw
    /// source would report an addition the deploy never makes.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DryRun_DiffsThePlan_NotTheRawSource()
    {
        var rawSource = SnapshotWithMeasure(includeMeasure: true);
        var target = SnapshotWithMeasure(includeMeasure: false);
        var provider = new DirectionalDeployProvider(
            reference => reference.Value == "local-model" ? rawSource : target,
            () => new ModelDeployPlan(TargetExists: true, target, Planned: target));

        var handler = new DeployModelHandler([provider], TestState);
        var result = await handler.HandleAsync(DryRunRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Diff!.HasChanges);
        Assert.Empty(result.Data.Diff.Changes);
    }

    /// <summary>
    /// A target database that does not exist yet has nothing to diff against: the deploy creates
    /// it with the full source model, and the dry run must say so rather than report every object
    /// in the model as an addition or fail as "target unreachable".
    /// </summary>
    [Fact]
    public async Task HandleAsync_DryRun_TargetMissing_ReportsCreatesDatabase()
    {
        var source = SnapshotWithMeasure(includeMeasure: true);
        var provider = new DirectionalDeployProvider(
            _ => source,
            () => new ModelDeployPlan(TargetExists: false, Target: null, Planned: source));

        var handler = new DeployModelHandler([provider], TestState);
        var result = await handler.HandleAsync(DryRunRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.CreatesDatabase);
        Assert.Null(result.Data.Diff);
        Assert.Null(result.Data.DiffError);
    }

    /// <summary>
    /// An unreadable target degrades the preview instead of failing the command: the dry run
    /// still succeeds, reporting why the diff is missing.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DryRun_PlanFailure_SetsDiffError_AndStillSucceeds()
    {
        var source = SnapshotWithMeasure(includeMeasure: true);
        var provider = new DirectionalDeployProvider(
            _ => source,
            () => throw new InvalidOperationException("workspace is unreachable"));

        var handler = new DeployModelHandler([provider], TestState);
        var result = await handler.HandleAsync(DryRunRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("dry-run", result.Data!.Status);
        Assert.Null(result.Data.Diff);
        Assert.Contains("workspace is unreachable", result.Data.DiffError);
    }

    private static DeployModelRequest DryRunRequest()
        => new(
            new ModelReference("local-model"),
            Server: "my-workspace",
            Database: "my-model",
            Profile: null,
            CreateOnly: false,
            SkipBpa: true,
            FixBpa: false,
            BpaRules: null,
            XmlaOutput: null,
            Force: false,
            Ci: null,
            DryRun: true);

    private static ModelSnapshot SnapshotWithMeasure(bool includeMeasure)
    {
        var children = new List<ModelObject>();
        if (includeMeasure)
            children.Add(new ModelObject(
                "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
                Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
                SourceColumn: null, Children: []));

        var table = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: children);

        return new ModelSnapshot("stub", 1601, [table]);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var handler = new DeployModelHandler([], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("missing.bim"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoTargetServer()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState, () => null);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: null,
                Database: null,
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DEPLOY_NO_TARGET", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_GeneratesScript_WhenXmlaOutputIsDash()
    {
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: "-",
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("script", result.Data!.Status);
        Assert.Equal("-", result.Data.ScriptPath);
        Assert.NotNull(result.Data.Script);
        Assert.Contains("my-model", result.Data.Script);
    }

    [Fact]
    public async Task HandleAsync_GeneratesScript_WhenXmlaOutputIsFile()
    {
        using var outputDir = new TempDir();
        var scriptPath = outputDir.Combine("script.json");
        var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: scriptPath,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("script", result.Data!.Status);
        Assert.True(File.Exists(result.Data.ScriptPath));
        var content = await File.ReadAllTextAsync(result.Data.ScriptPath!);
        Assert.Contains("my-model", content);
    }

    [Fact]
    public async Task HandleAsync_Fails_WhenFixBpaRequestedOnNonMutationSession()
    {
        // The snapshot carries an empty role so the bundled REMOVE_ROLES_WITH_NO_MEMBERS rule
        // fires at least one violation; that is required to reach the --fix-bpa branch, which
        // then fails because the deploy-only session cannot apply fixes.
        var session = new StubDeployOnlySession();
        var handler = new DeployModelHandler([new StubDeployOnlyProvider(session)], TestState);
        var result = await handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("samples/basic-tmdl"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: false,
                FixBpa: true,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DEPLOY_FIX_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.False(session.DeployCalled);
    }

    [Fact]
    public async Task HandleAsync_LetsModelLoadExceptionPropagate()
    {
        var handler = new DeployModelHandler([new BrokenModelDeployProvider()], TestState, () => null);

        // The source model being unloadable is not a deploy failure: it must reach the CLI's
        // top-level TOMIX_MODEL_LOAD_FAILED handler instead of becoming TOMIX_DEPLOY_FAILED.
        await Assert.ThrowsAsync<ModelLoadException>(() => handler.HandleAsync(
            new DeployModelRequest(
                new ModelReference("broken.bim"),
                Server: "my-workspace",
                Database: "my-model",
                Profile: null,
                CreateOnly: false,
                SkipBpa: true,
                FixBpa: false,
                BpaRules: null,
                XmlaOutput: null,
                Force: false,
                Ci: null),
            CancellationToken.None));
    }

    private sealed class BrokenModelDeployProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new BrokenModelDeploySession());
    }

    /// <summary>Loads its model lazily like the real file sessions: deploy is the first touch.</summary>
    private sealed class BrokenModelDeploySession : IModelSession, IModelDeploySession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => throw Load();

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => throw Load();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => throw Load();

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => throw Load();

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => throw Load();

        private static ModelLoadException Load()
            => new("Cannot load model from 'broken.bim': unparsable.", new InvalidOperationException("inner"));
    }

    /// <summary>
    /// Serves a different snapshot per reference — the raw source for the local model, the live
    /// model for the remote target — plus the deploy plan the dry run is supposed to diff. The
    /// two are configured independently so a handler that went back to diffing the raw source
    /// instead of the plan produces a visibly different answer.
    /// </summary>
    private sealed class DirectionalDeployProvider(
        Func<ModelReference, ModelSnapshot> snapshots,
        Func<ModelDeployPlan>? plan = null) : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new DirectionalDeploySession(snapshots(reference), plan));
    }

    private sealed class DirectionalDeploySession(ModelSnapshot snapshot, Func<ModelDeployPlan>? plan)
        : IModelSession, IModelDeploySession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "created", 42));

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult("{}");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => plan is null
                ? throw new InvalidOperationException("This session has no configured deploy plan.")
                : Task.FromResult(plan());
    }

    private sealed class StubDeployProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubDeploySession());
    }

    private sealed class StubDeploySession : IModelSession, IModelDeploySession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 3, 12, 4, 2, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "created", 42));

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult($"{{\"createOrReplace\":{{\"object\":{{\"database\":\"{request.Database ?? "stub"}\"}},\"database\":{{\"name\":\"{request.Database ?? "stub"}\",\"compatibilityLevel\":1601}}}}}}");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(new ModelDeployPlan(
                TargetExists: true,
                new ModelSnapshot("stub", 1601, []),
                new ModelSnapshot("stub", 1601, [])));
    }

    private sealed class StubDeployOnlyProvider : IModelProvider
    {
        private readonly StubDeployOnlySession _session;

        public StubDeployOnlyProvider(StubDeployOnlySession session) => _session = session;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    /// <summary>
    /// A deploy-capable session that does NOT implement <see cref="IModelMutationSession"/>,
    /// and whose snapshot carries an empty role so BPA rules fire. Tracks whether deploy was
    /// reached so the gate-failure test can assert the deploy never happened.
    /// </summary>
    private sealed class StubDeployOnlySession : IModelSession, IModelDeploySession
    {
        public bool DeployCalled { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 3, 12, 4, 2, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, [EmptyRole()]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            DeployCalled = true;
            return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "created", 42));
        }

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult($"{{\"createOrReplace\":{{\"object\":{{\"database\":\"{request.Database ?? "stub"}\"}},\"database\":{{\"name\":\"{request.Database ?? "stub"}\",\"compatibilityLevel\":1601}}}}}}");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken ct)
            => Task.FromResult(new ModelDeployPlan(
                TargetExists: true,
                new ModelSnapshot("stub", 1601, []),
                new ModelSnapshot("stub", 1601, [])));

        private static ModelObject EmptyRole()
            => new("Empty", ModelObjectKind.Role, "Roles/Empty",
                Detail: null,
                Expression: null,
                Description: "desc",
                Hidden: false,
                SourceColumn: null,
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "ModelRole", ["RlsExpression"] = "" });
    }
}
