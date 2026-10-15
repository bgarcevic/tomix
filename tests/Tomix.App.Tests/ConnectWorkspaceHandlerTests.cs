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
        var provider = new StubRemoteProvider(summaryDatabaseName: null, listed: ["Other", "Contoso_Core"]);
        var handler = new ConnectWorkspaceHandler([provider]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "contoso_core"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Exists, probe.Status);
        Assert.Equal("Contoso_Core", probe.ResolvedDatabase);
        Assert.Equal(0, provider.Opens); // the listing answers it; the model tree is never loaded
    }

    [Fact]
    public async Task Probe_CaseVariantsListed_PrefersExactSpelling()
    {
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(summaryDatabaseName: null, listed: ["SALES", "Sales"])]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal("Sales", probe.ResolvedDatabase);
    }

    [Fact]
    public async Task Probe_TargetOpens_NoReportedName_KeepsRequested()
    {
        // No catalog to report a spelling: the target is opened, and the requested name stands.
        var handler = new ConnectWorkspaceHandler([new StubRemoteProvider(summaryDatabaseName: null)]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Exists, probe.Status);
        Assert.Equal("Sales", probe.ResolvedDatabase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_DatabaseNotFound_ReportsMissing(bool canList)
    {
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(
                summaryDatabaseName: null,
                openError: new ModelConnectionException(
                    ModelConnectionFailureKind.DatabaseNotFound,
                    "Database not found on endpoint: 'Sales'"),
                listed: canList ? ["Other"] : null)]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Missing, probe.Status);
        Assert.Contains(probe.Diagnostics, d => d.Code == "TOMIX_DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task Probe_NotListedButUnreadable_ReportsUnreachableNotMissing()
    {
        // Read-only (Build) permission hides the dataset from the list; it still exists, so
        // a mirror must not be saved against its name.
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(
                summaryDatabaseName: null,
                openError: new ModelConnectionException(
                    ModelConnectionFailureKind.MetadataUnavailable,
                    "'Sales' can be queried, but its metadata cannot be read."),
                listed: [])]);

        var probe = await handler.ProbeAsync(new ConnectWorkspaceProbeRequest(Workspace, "Sales"), CancellationToken.None);

        Assert.Equal(ConnectWorkspaceProbeStatus.Unreachable, probe.Status);
        Assert.Contains(probe.Diagnostics, d => d.Code == "TOMIX_METADATA_ACCESS_DENIED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_ServerUnreachable_ReportsUnreachableWithDiagnostics(bool canList)
    {
        var error = new TimeoutException("no route to host");
        var handler = new ConnectWorkspaceHandler(
            [new StubRemoteProvider(summaryDatabaseName: null, openError: error, listError: canList ? error : null)]);

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
            new ConnectWorkspaceInitRequest(workspace, WorkspaceFormat: null, Overwrite: false, Primary: new ModelReference("./model.bim")),
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
            new ConnectWorkspaceInitRequest(workspace, WorkspaceFormat: "bim", Overwrite: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.True(init.Initialized);
        Assert.Equal("bim", init.Serialization);
        Assert.Equal(Path.Combine(workspace, "model.bim"), session.LastRequest!.OutputPath);
    }

    [Fact]
    public async Task Initialize_ExistingTargetWithoutOverwrite_Skips()
    {
        using var workspace = new TempDir();
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace.Path, WorkspaceFormat: null, Overwrite: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
        Assert.Null(session.LastRequest);
        Assert.True(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task Initialize_ExistingTargetWithOverwrite_DeletesThenExports()
    {
        using var workspace = new TempDir();
        workspace.WriteFile("stale.txt", "old");
        var session = new RecordingExportSession();
        var handler = new ConnectWorkspaceHandler([new StubExportProvider("./model.bim", session)]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(workspace.Path, WorkspaceFormat: null, Overwrite: true, Primary: new ModelReference("./model.bim")),
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
            new ConnectWorkspaceInitRequest(FreshPath(), WorkspaceFormat: null, Overwrite: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
    }

    [Fact]
    public async Task Initialize_SessionCannotExport_SilentlySkips()
    {
        var handler = new ConnectWorkspaceHandler([new StubRemoteProvider(summaryDatabaseName: null, expectedPath: "./model.bim")]);

        var init = await handler.InitializeAsync(
            new ConnectWorkspaceInitRequest(FreshPath(), WorkspaceFormat: null, Overwrite: false, Primary: new ModelReference("./model.bim")),
            CancellationToken.None);

        Assert.False(init.Initialized);
    }

    private static string FreshPath()
        => Path.Combine(Path.GetTempPath(), "tomix-ws-" + Guid.NewGuid().ToString("N"));

    // --- stubs -----------------------------------------------------------------------------------

    /// <summary>Opens any remote reference (or a specific path) with a plain, non-exporting session.
    /// Given <c>listed</c> or <c>listError</c>, it also lists the endpoint's databases.</summary>
    private sealed class StubRemoteProvider : IModelProvider, IServerCatalog
    {
        private readonly string? _summaryDatabaseName;
        private readonly Exception? _openError;
        private readonly string? _expectedPath;
        private readonly string[]? _listed;
        private readonly Exception? _listError;

        public StubRemoteProvider(
            string? summaryDatabaseName,
            Exception? openError = null,
            string? expectedPath = null,
            string[]? listed = null,
            Exception? listError = null)
        {
            _summaryDatabaseName = summaryDatabaseName;
            _openError = openError;
            _expectedPath = expectedPath;
            _listed = listed;
            _listError = listError;
        }

        public int Opens { get; private set; }

        public bool CanOpen(ModelReference reference)
            => _expectedPath is null ? reference.IsRemote : reference.Value == _expectedPath;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
        {
            Opens++;
            return _openError is null
                ? Task.FromResult<IModelSession>(new StubSession(_summaryDatabaseName))
                : Task.FromException<IModelSession>(_openError);
        }

        public bool CanList(ModelReference endpoint) => (_listed is not null || _listError is not null) && endpoint.IsRemote;

        public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(ModelReference endpoint, CancellationToken ct)
            => _listError is null
                ? Task.FromResult<IReadOnlyList<ServerDatabaseInfo>>(_listed!.Select(n => new ServerDatabaseInfo(n)).ToList())
                : Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(_listError);
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
