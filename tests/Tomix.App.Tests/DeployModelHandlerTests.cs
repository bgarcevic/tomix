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
                DeployFull: false,
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
                DeployFull: false,
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
                DeployFull: false,
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
                DeployFull: false,
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
        var outputDir = Path.Combine(Path.GetTempPath(), $"tomix-deploy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var scriptPath = Path.Combine(outputDir, "script.json");
            var handler = new DeployModelHandler([new StubDeployProvider()], TestState);
            var result = await handler.HandleAsync(
                new DeployModelRequest(
                    new ModelReference("samples/basic-tmdl"),
                    Server: "my-workspace",
                    Database: "my-model",
                    Profile: null,
                    DeployFull: false,
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
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
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
                DeployFull: false,
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
                DeployFull: false,
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

        public string GenerateScript(ModelDeployRequest request)
            => throw Load();

        private static ModelLoadException Load()
            => new("Cannot load model from 'broken.bim': unparsable.", new InvalidOperationException("inner"));
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

        public string GenerateScript(ModelDeployRequest request)
            => $"{{\"createOrReplace\":{{\"object\":{{\"database\":\"{request.Database ?? "stub"}\"}},\"database\":{{\"name\":\"{request.Database ?? "stub"}\",\"compatibilityLevel\":1601}}}}}}";
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

        public string GenerateScript(ModelDeployRequest request)
            => $"{{\"createOrReplace\":{{\"object\":{{\"database\":\"{request.Database ?? "stub"}\"}},\"database\":{{\"name\":\"{request.Database ?? "stub"}\",\"compatibilityLevel\":1601}}}}}}";

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
