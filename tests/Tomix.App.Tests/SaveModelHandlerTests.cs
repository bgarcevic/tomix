using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.App.Save;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class SaveModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_SyncsToWorkspace_WhenSyncTargetSet()
    {
        using var dir = new TempDir();
        var handler = new SaveModelHandler([new StubSaveProvider(dir.Path, deploySucceeds: true)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SyncStatus.Succeeded, result.Data!.Sync.Status);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws / MyModel", result.Data.Sync.Target);
        Assert.Null(result.Data.Sync.Warning);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_SetsWarning_WhenSyncFails()
    {
        using var dir = new TempDir();
        var handler = new SaveModelHandler([new StubSaveProvider(dir.Path, deploySucceeds: false)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SyncStatus.Failed, result.Data!.Sync.Status);
        Assert.NotNull(result.Data.Sync.Warning);
        Assert.Contains("sync failed", result.Data.Sync.Warning, StringComparison.OrdinalIgnoreCase);
        // The result still renders, but the exit code flags the mirror drift for CI.
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_SkipsSync_WhenNoSyncTarget()
    {
        using var dir = new TempDir();
        var handler = new SaveModelHandler([new StubSaveProvider(dir.Path, deploySucceeds: true)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SyncStatus.NotConfigured, result.Data!.Sync.Status);
        Assert.Null(result.Data.Sync.Target);
        Assert.Null(result.Data.Sync.Warning);
    }

    [Fact]
    public async Task HandleAsync_SetsWarning_WhenSessionCannotDeploy()
    {
        using var dir = new TempDir();
        var handler = new SaveModelHandler([new StubExportOnlyProvider(dir.Path)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                SyncTarget: new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SyncStatus.Skipped, result.Data!.Sync.Status);
        Assert.NotNull(result.Data.Sync.Warning);
        Assert.Contains("does not support deploy", result.Data.Sync.Warning, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Issue #253 consistency: an unevaluable rule blocks the save gate -----
    // Saving can never remediate a rule that cannot be evaluated, so remaining rule errors must
    // block regardless of how many other fixes --fix-bpa applied.

    [Fact]
    public async Task HandleAsync_BpaGate_UncompilableRule_BlocksEvenWhenOtherFixesApply()
    {
        using var dir = new TempDir();
        var brokenPath = WriteRuleFile(dir, "broken.json", BrokenRuleJson);
        var fixablePath = WriteRuleFile(dir, "fixable.json", FixableRuleJson);

        var session = new StubGateSession(dir.Path);
        var handler = new SaveModelHandler([new StubGateProvider(dir.Path, session)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                FixBpa: true,
                BpaRules: [brokenPath, fixablePath]),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not be evaluated", result.Diagnostics[0].Message);
        Assert.Contains("QA_BROKEN_RULE", result.Diagnostics[0].Message);
        Assert.False(session.ExportCalled); // the gate blocks before anything is written
    }

    [Fact]
    public async Task HandleAsync_BpaGate_RuleErrorOnly_BlocksSave()
    {
        using var dir = new TempDir();
        var brokenPath = WriteRuleFile(dir, "broken.json", BrokenRuleJson);

        var session = new StubGateSession(dir.Path);
        var handler = new SaveModelHandler([new StubGateProvider(dir.Path, session)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                FixBpa: true,
                BpaRules: [brokenPath]),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", result.Diagnostics[0].Code);
        Assert.False(session.ExportCalled);
    }

    [Fact]
    public async Task HandleAsync_BpaGate_FixableFindingsOnly_StillSaves()
    {
        // Pins the fix-what-you-can flow: findings with fixes applied (no rule errors) save.
        using var dir = new TempDir();
        var fixablePath = WriteRuleFile(dir, "fixable.json", FixableRuleJson);

        var session = new StubGateSession(dir.Path);
        var handler = new SaveModelHandler([new StubGateProvider(dir.Path, session)]);
        var result = await handler.HandleAsync(
            new SaveModelRequest(
                Model: new ModelReference(dir.Path),
                OutputPath: dir.Path,
                Serialization: "tmdl",
                Overwrite: true,
                SupportingFiles: false,
                FixBpa: true,
                BpaRules: [fixablePath]),
            CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(session.ExportCalled);
    }

    private static string WriteRuleFile(TempDir dir, string fileName, string json)
    {
        var path = dir.Combine(fileName);
        File.WriteAllText(path, json);
        return path;
    }

    // Warning-severity on purpose: the finding must still be error-severity.
    private const string BrokenRuleJson =
        "[{\"ID\":\"QA_BROKEN_RULE\",\"Name\":\"broken rule\",\"Category\":\"qa\",\"Severity\":2,\"Scope\":\"ModelRole\",\"Expression\":\"ThisIsNotARealMember = 1\",\"CompatibilityLevel\":1200}]";

    // Fires on the stub's empty role and carries a non-destructive assignment fix, so
    // --fix-bpa applies it (FixesApplied > 0) — the case that used to mask the broken rule.
    private const string FixableRuleJson =
        "[{\"ID\":\"QA_FIXABLE\",\"Name\":\"fixable rule\",\"Category\":\"qa\",\"Severity\":2,\"Scope\":\"ModelRole\",\"Expression\":\"Members.Count() == 0\",\"FixExpression\":\"IsHidden = true\",\"CompatibilityLevel\":1200}]";

    private sealed class StubSaveProvider : IModelProvider
    {
        private readonly string _exportDir;
        private readonly bool _deploySucceeds;

        public StubSaveProvider(string exportDir, bool deploySucceeds)
        {
            _exportDir = exportDir;
            _deploySucceeds = deploySucceeds;
        }

        public bool CanOpen(ModelReference reference) => reference.Value == _exportDir;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSaveSession(_exportDir, _deploySucceeds));
    }

    private sealed class StubSaveSession : IModelSession, IModelExportSession, IModelDeploySession
    {
        private readonly string _exportDir;
        private readonly bool _deploySucceeds;

        public StubSaveSession(string exportDir, bool deploySucceeds)
        {
            _exportDir = exportDir;
            _deploySucceeds = deploySucceeds;
        }

        public string SourcePath => _exportDir;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(_exportDir, request.Serialization));

        public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken ct)
        {
            if (_deploySucceeds)
                return Task.FromResult(new ModelDeployResult(request.Server, request.Database ?? "stub", "updated", 42));

            throw new InvalidOperationException("Deploy failed for test purposes.");
        }

        public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken) => Task.FromResult("");

        public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This stub never plans a deploy.");
    }

    [Fact]
    public async Task ALiveSessionOnAServer_SavesToTheServer_WithoutAnOutputPath()
    {
        var session = new LiveStubSession(sourcePath: "");
        var result = await new SaveModelHandler(new LiveStubSource(session)).HandleAsync(LiveSave(Desktop), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, session.Saves);
        Assert.Equal(0, session.Exports);
        Assert.Equal("localhost:51234 / Sales", result.Data!.Outcome.SavedTo);
    }

    [Fact]
    public async Task ALiveSessionOnAServer_WritesOnlyACopy_WithAnOutputPath()
    {
        using var dir = new TempDir();
        var session = new LiveStubSession(sourcePath: "");
        var result = await new SaveModelHandler(new LiveStubSource(session)).HandleAsync(
            LiveSave(Desktop) with { OutputPath = dir.Path }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, session.Saves);
        Assert.Equal(1, session.Exports);
    }

    [Fact]
    public async Task ALiveSession_NamingItsOwnFiles_SavesInPlace()
    {
        using var dir = new TempDir();
        var session = new LiveStubSession(sourcePath: dir.Path);
        var result = await new SaveModelHandler(new LiveStubSource(session)).HandleAsync(
            LiveSave(new ModelReference(dir.Path)) with { OutputPath = dir.Path + Path.DirectorySeparatorChar }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, session.Saves);
        Assert.Equal(0, session.Exports);
    }

    [Fact]
    public async Task AServerModelChangedOutsideTheSession_FailsAsStale_WithoutOfferingReload()
    {
        var session = new LiveStubSession(sourcePath: "") { Fails = new ModelSourceChangedException("'Sales' on localhost:51234", canReload: false) };
        var result = await new SaveModelHandler(new LiveStubSource(session)).HandleAsync(LiveSave(Desktop), CancellationToken.None);

        var failure = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_SESSION_STALE", failure.Code);
        Assert.Contains("save --force", failure.Hint);
        Assert.DoesNotContain("'reload'", failure.Hint);
    }

    [Fact]
    public async Task AServerThatIsGone_FailsAsUnavailable_AndPointsAtSavingACopy()
    {
        var session = new LiveStubSession(sourcePath: "")
        {
            Fails = new ModelSourceUnavailableException("'Sales' on localhost:51234", "The Power BI Desktop instance at localhost:51234 is no longer running.")
        };
        var result = await new SaveModelHandler(new LiveStubSource(session)).HandleAsync(LiveSave(Desktop), CancellationToken.None);

        var failure = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_SESSION_SOURCE_UNAVAILABLE", failure.Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("save -o", failure.Hint);
    }

    private static readonly ModelReference Desktop = new("localhost:51234", "Sales");

    private static SaveModelRequest LiveSave(ModelReference model)
        => new(model, OutputPath: null, Serialization: "", Overwrite: false, SupportingFiles: false);

    private sealed class LiveStubSource(IModelSession session) : IModelSessionSource
    {
        public bool IsLive => true;

        public Task<ModelSessionLease> LeaseAsync(ModelReference model, CancellationToken cancellationToken)
            => Task.FromResult(ModelSessionLease.OneShot(session));
    }

    /// <summary>A live session's lease view: its own save writes the source, export a copy.</summary>
    private sealed class LiveStubSession(string sourcePath) : IModelSession, IModelExportSession, IModelMutationSession
    {
        public int Saves { get; private set; }

        public int Exports { get; private set; }

        public Exception? Fails { get; init; }

        public string SourcePath => sourcePath;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("Sales", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("Sales", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
        {
            Exports++;
            return Task.FromResult(new ModelExportResult(request.OutputPath, request.Serialization));
        }

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
        {
            if (Fails is not null)
                throw Fails;
            Saves++;
            return Task.FromResult(new ModelExportResult(sourcePath.Length == 0 ? "Sales" : sourcePath, "tmdl"));
        }

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw new NotSupportedException();

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => throw new NotSupportedException();

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
    }

    private sealed class StubExportOnlyProvider : IModelProvider
    {
        private readonly string _exportDir;

        public StubExportOnlyProvider(string exportDir) => _exportDir = exportDir;

        public bool CanOpen(ModelReference reference) => reference.Value == _exportDir;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubExportOnlySession(_exportDir));
    }

    private sealed class StubExportOnlySession : IModelSession, IModelExportSession
    {
        private readonly string _exportDir;

        public StubExportOnlySession(string exportDir) => _exportDir = exportDir;

        public string SourcePath => _exportDir;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(_exportDir, request.Serialization));
    }

    /// <summary>
    /// Gate-test session: one empty role in the snapshot so custom ModelRole-scoped rules fire,
    /// mutable so --fix-bpa passes the mutation-session check, and export is observed so a
    /// blocked save can assert nothing was written.
    /// </summary>
    private sealed class StubGateProvider(string dirPath, StubGateSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.Value == dirPath;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class StubGateSession(string exportDir) : IModelSession, IModelExportSession, IModelMutationSession
    {
        public bool ExportCalled { get; private set; }

        public string SourcePath => exportDir;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, [EmptyRole()]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
        {
            ExportCalled = true;
            return Task.FromResult(new ModelExportResult(exportDir, request.Serialization));
        }

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => throw new NotSupportedException("This stub never adds objects.");

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => new(request.Path, Changed: true,
                Property: request.Properties[0].Property, Value: request.Properties[0].Value);

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: false);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
            => Task.FromResult(new ModelExportResult(exportDir, serialization));

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
