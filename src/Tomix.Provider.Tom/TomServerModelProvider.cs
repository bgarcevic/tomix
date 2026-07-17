using Tomix.Core.Authentication;
using Tomix.Core.Models;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;
using TabularDatabase = Microsoft.AnalysisServices.Tabular.Database;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;

namespace Tomix.Provider.Tom;

/// <summary>
/// Opens a live model over XMLA (<c>powerbi://</c>, <c>asazure://</c>, or a local
/// <c>localhost:&lt;port&gt;</c> Power BI Desktop instance). Remote endpoints acquire a token
/// from the injected <see cref="IAccessTokenProvider"/>; local instances connect without one.
/// Supports read operations (summary, snapshot), mutation (add/set/rm/replace), deploy, and export.
/// Mutations are persisted to the server via <c>Model.SaveChanges()</c> on save.
/// </summary>
public sealed class TomServerModelProvider : IModelProvider, IServerCatalog
{
    private readonly IAccessTokenProvider? _tokenProvider;

    public TomServerModelProvider(IAccessTokenProvider? tokenProvider) => _tokenProvider = tokenProvider;

    public bool CanOpen(ModelReference reference) => reference.IsRemote;

    public async Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
    {
        var server = await ConnectServerAsync(reference, cancellationToken).ConfigureAwait(false);
        return new TomServerModelSession(server, ResolveDatabase(server, reference.Database), reference, _tokenProvider);
    }

    public bool CanList(ModelReference endpoint) => endpoint.IsRemote;

    public async Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(
        ModelReference endpoint,
        CancellationToken cancellationToken)
    {
        var server = await ConnectServerAsync(endpoint with { Database = null }, cancellationToken).ConfigureAwait(false);
        try
        {
            var databases = new List<ServerDatabaseInfo>(server.Databases.Count);
            foreach (TabularDatabase database in server.Databases)
            {
                databases.Add(new ServerDatabaseInfo(
                    string.IsNullOrWhiteSpace(database.Name) ? database.ID : database.Name,
                    database.CompatibilityLevel,
                    database.LastUpdate == default
                        ? null
                        : new DateTimeOffset(DateTime.SpecifyKind(database.LastUpdate, DateTimeKind.Utc))));
            }

            return databases;
        }
        finally
        {
            if (server.Connected)
                server.Disconnect();
            server.Dispose();
        }
    }

    private async Task<TabularServer> ConnectServerAsync(ModelReference reference, CancellationToken cancellationToken)
    {
        var server = new TabularServer();

        if (!reference.IsLocalInstance)
        {
            if (_tokenProvider is null)
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

            var token = await _tokenProvider.GetTokenAsync(reference.Value, cancellationToken).ConfigureAwait(false);
            server.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
            server.OnAccessTokenExpired = _ =>
            {
                var refreshed = _tokenProvider.GetTokenAsync(reference.Value, cancellationToken)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                return new AsAccessToken(refreshed.Token, refreshed.ExpiresOn.UtcDateTime);
            };
        }

        server.Connect(BuildConnectionString(reference));
        return server;
    }

    internal static string BuildConnectionString(ModelReference reference)
    {
        var connectionString = $"Data Source={TomModelDeployer.ResolveEndpoint(reference.Value)}";
        return string.IsNullOrWhiteSpace(reference.Database)
            ? connectionString
            : $"{connectionString};Initial Catalog={reference.Database}";
    }

    private static TabularDatabase ResolveDatabase(TabularServer server, string? database)
    {
        if (!string.IsNullOrWhiteSpace(database))
            return server.Databases.FindByName(database)
                ?? throw new InvalidOperationException($"Database not found on endpoint: {database}");

        return server.Databases.Count switch
        {
            1 => server.Databases[0],
            0 => throw new InvalidOperationException("No databases are available on the endpoint."),
            _ => throw new InvalidOperationException("Multiple databases on the endpoint; specify one with --database.")
        };
    }
}

internal sealed class TomServerModelSession : IModelSession, IModelExportSession, IModelMutationSession, IModelDeploySession, IModelRefreshSession, IModelQuerySession
{
    private readonly TabularServer _server;
    private readonly TabularDatabase _database;
    private readonly ModelReference _reference;
    private readonly IAccessTokenProvider? _tokenProvider;

    public TomServerModelSession(TabularServer server, TabularDatabase database, ModelReference reference, IAccessTokenProvider? tokenProvider)
    {
        _server = server;
        _database = database;
        _reference = reference;
        _tokenProvider = tokenProvider;
    }

    public string SourcePath => "";

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ModelName();
        return Task.FromResult(TomModelSummarizer.Summarize(_database, name)
            with { DatabaseName = string.IsNullOrWhiteSpace(_database.Name) ? null : _database.Name });
    }

    public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TomModelSummarizer.Snapshot(_database, ModelName()));
    }

    public ValueTask DisposeAsync()
    {
        if (_server.Connected)
            _server.Disconnect();

        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<ModelExportResult> ExportAsync(
        ModelExportRequest request,
        CancellationToken cancellationToken)
        => TomModelExporter.ExportAsync(_database, request, cancellationToken);

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        => new TomModelMutator(_database).AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        => new TomModelMutator(_database).SetProperty(request);

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
        => new TomModelMutator(_database).RemoveObject(request);

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
        => new TomModelMutator(_database).ReplaceText(request);

    public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        => new TomModelMutator(_database).RewriteExpressions(edits);

    public RefreshPolicyInfo? GetRefreshPolicy(string table)
        => new TomRefreshPolicyManager(_database).Get(table);

    public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request)
        => new TomRefreshPolicyManager(_database).Set(request);

    public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false)
        => new TomRefreshPolicyManager(_database).Remove(table, ifExists);

    public Task<ModelExportResult> SaveAsync(
        string? outputPath,
        string serialization,
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Database.Update() without options alters only the database object itself; model-tree
        // changes (measures, properties, annotations) silently vanish. SaveChanges() sends the
        // incremental model edits, and its result can carry XMLA errors without throwing.
        var result = _database.Model.SaveChanges();
        if (result.XmlaResults is { } xmlaResults)
        {
            var serverErrors = new List<string>();
            XmlaResultHelper.ExtractMessages(xmlaResults, serverErrors);
            if (serverErrors.Count > 0)
                throw new InvalidOperationException(
                    $"The server rejected the save: {string.Join("; ", serverErrors)}");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
            return Task.FromResult(new ModelExportResult(ModelName(), "remote"));

        return TomModelExporter.ExportAsync(
            _database,
            new ModelExportRequest(outputPath, string.IsNullOrWhiteSpace(serialization) ? "tmdl" : serialization, force, SupportingFiles: false),
            cancellationToken);
    }

    public Task<ModelDeployResult> DeployAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken)
        => TomModelDeployer.DeployAsync(_database, request, _tokenProvider, cancellationToken);

    public string GenerateScript(ModelDeployRequest request)
        => TomModelDeployer.GenerateScript(_database, request);

    public Task<ModelRefreshResult> RefreshAsync(
        ModelRefreshRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
        => TomModelRefresher.RefreshAsync(_server, _database, request, progress, traceWriter, cancellationToken);

    public string GenerateRefreshScript(ModelRefreshRequest request)
        => TomModelRefresher.GenerateRefreshScript(_database, request);

    public Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(
        RefreshPolicyApplyRequest request,
        CancellationToken cancellationToken)
        => TomRefreshPolicyApplier.ApplyAsync(_server, _database, request, cancellationToken);

    public Task<ModelQueryResult> ExecuteQueryAsync(
        ModelQueryRequest request,
        CancellationToken cancellationToken)
        // Rebuild the connection string with the *resolved* database so single-database
        // endpoints opened without --database still target the right catalog over ADOMD.
        => TomModelQueryExecutor.ExecuteAsync(
            TomServerModelProvider.BuildConnectionString(_reference with { Database = ModelName() }),
            _reference,
            ModelName(),
            _tokenProvider,
            request,
            cancellationToken);

    private string ModelName()
        => string.IsNullOrWhiteSpace(_database.Name) ? _database.ID : _database.Name;
}
