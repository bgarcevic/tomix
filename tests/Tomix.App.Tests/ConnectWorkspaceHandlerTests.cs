using Tomix.App.Connect;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public class ConnectWorkspaceHandlerTests
{
    private const string Workspace = "powerbi://api.powerbi.com/v1.0/myorg/WS";

    // --- ProbeAsync -----------------------------------------------------------------------------

    [Fact]
    public async Task Probe_TargetOpens_ReportsExistsWithCanonicalName()
    {
        var handler = new ConnectWorkspaceHandler([new StubRemoteProvider(summaryDatabaseName: "Contoso_Core")]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "contoso_core"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Exists, probe.Status);
        Assert.Equal("Contoso_Core", probe.ResolvedDatabase);
    }

    [Fact]
    public async Task Probe_TargetOpens_NoReportedName_KeepsRequested()
    {
        var handler = new ConnectWorkspaceHandler([new StubRemoteProvider(summaryDatabaseName: null)]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Exists, probe.Status);
        Assert.Equal("Sales", probe.ResolvedDatabase);
    }

    [Fact]
    public async Task Probe_DatabaseNotFound_ReportsMissing()
    {
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(
                summaryDatabaseName: null,
                openError: new ModelConnectionException(
                    ModelConnectionFailureKind.DatabaseNotFound,
                    "Database not found on endpoint: 'Sales'"))]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Missing, probe.Status);
        Assert.Contains(probe.Diagnostics, d => d.Code == "TOMIX_DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task Probe_ServerUnreachable_ReportsUnreachableWithDiagnostics()
    {
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(summaryDatabaseName: null, openError: new TimeoutException("no route to host"))]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Unreachable, probe.Status);
        Assert.NotEmpty(probe.Diagnostics);
        Assert.NotEqual(0, probe.ExitCode);
    }

    // --- InitializeAsync ------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_FreshTarget_ExportsWithoutSupportingFiles()
    {
        var workspace = FreshPath();
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace, WorkspaceFormat: null, Force: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.True(init.Initialized);
        Assert.Equal("tmdl", init.Serialization);
        Assert.NotNull(session.LastRequest);
        Assert.Equal(workspace, session.LastRequest!.OutputPath);
        Assert.Equal("tmdl", session.LastRequest.Serialization);
        Assert.True(session.LastRequest.Overwrite);
        Assert.False(session.LastRequest.SupportingFiles);
    }

    [Fact]
    public async Task Initialize_BimFormat_TargetsModelBimInsideFolder()
    {
        var workspace = FreshPath();
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace, WorkspaceFormat: "bim", Force: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.True(init.Initialized);
        Assert.Equal("bim", init.Serialization);
        Assert.Equal(Path.Combine(workspace, "model.bim"), session.LastRequest!.OutputPath);
    }

    [Fact]
    public async Task Initialize_ExistingTargetWithoutForce_Skips()
    {
        using var workspace = new TempDir();
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace.Path, WorkspaceFormat: null, Force: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
        Assert.Null(session.LastRequest);
        Assert.True(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task Initialize_ExistingTargetWithForce_DeletesThenExports()
    {
        using var workspace = new TempDir();
        workspace.WriteFile("stale.txt", "old");
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace.Path, WorkspaceFormat: null, Force: true, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.True(init.Initialized);
        Assert.NotNull(session.LastRequest);
        Assert.False(Directory.Exists(workspace.Path)); // stub export doesn't recreate it
    }

    [Fact]
    public async Task Initialize_NoProviderForPrimary_SilentlySkips()
    {
        var handler = new ConnectWorkspaceHandler([]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(FreshPath(), WorkspaceFormat: null, Force: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
    }

    [Fact]
    public async Task Initialize_SessionCannotExport_SilentlySkips()
    {
        var handler = new ConnectWorkspaceHandler([new StubRemoteProvider(summaryDatabaseName: null, expectedPath: "./model.bim")]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(FreshPath(), WorkspaceFormat: null, Force: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
    }

    private static string FreshPath()
        => Path.Combine(Path.GetTempPath(), "tomix-ws-" + Guid.NewGuid().ToString("N"));

    // --- stubs -----------------------------------------------------------------------------------

    /// <summary>Opens any remote reference (or a specific path) with a plain, non-exporting session.</summary>
    private sealed class StubRemoteProvider : IModelProvider
    {
        private readonly string? _summaryDatabaseName;
        private readonly Exception? _openError;
        private readonly string? _expectedPath;

        public StubRemoteProvider(string? summaryDatabaseName, Exception? openError = null, string? expectedPath = null)
        {
            _summaryDatabaseName = summaryDatabaseName;
            _openError = openError;
            _expectedPath = expectedPath;
        }

        public bool CanOpen(ModelReference reference)
            => _expectedPath is null ? reference.IsRemote : reference.Value == _expectedPath;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => _openError is null
                ? Task.FromResult<IModelSession>(new StubSession(_summaryDatabaseName))
                : Task.FromException<IModelSession>(_openError);
    }

    private sealed class StubSession : IModelSession
    {
        private readonly string? _databaseName;

        public StubSession(string? databaseName) => _databaseName = databaseName;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0, _databaseName));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubExportProvider : IModelProvider
    {
        private readonly string _expectedPath;
        private readonly RecordingExportSession _session;

        public StubExportProvider(string expectedPath, RecordingExportSession session)
        {
            _expectedPath = expectedPath;
            _session = session;
        }

        public bool CanOpen(ModelReference reference) => reference.Value == _expectedPath;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class RecordingExportSession : IModelSession, IModelExportSession
    {
        public ModelExportRequest? LastRequest { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new ModelExportResult(request.OutputPath, request.Serialization));
        }
    }
}
