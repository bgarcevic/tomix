using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tmdl;

public sealed class TmdlModelSession : IModelSession, IModelExportSession, IModelMutationSession,
    IExpressionRewriteSession, IObjectMoveSession, IRefreshPolicyMutationSession, IModelDeploySession
{
    private readonly string _path;
    private readonly IAccessTokenProvider? _tokenProvider;
    private Database? _database;

    public TmdlModelSession(string path, IAccessTokenProvider? tokenProvider = null)
    {
        _path = path;
        _tokenProvider = tokenProvider;
    }

    public string SourcePath => _path;

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = GetDatabase();
        return Task.FromResult(TomModelSummarizer.Summarize(database, ModelName(database))
            with
        { DatabaseName = string.IsNullOrWhiteSpace(database.Name) ? null : database.Name });
    }

    public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = GetDatabase();
        return Task.FromResult(TomModelSummarizer.Snapshot(database, ModelName(database)));
    }

    public ValueTask DisposeAsync()
    {
        _database = null;
        return ValueTask.CompletedTask;
    }

    public Task<ModelExportResult> ExportAsync(
        ModelExportRequest request,
        CancellationToken cancellationToken)
    {
        var effective = SamePath(request.OutputPath, _path)
            ? request with { Force = true }
            : request;
        return TomModelExporter.ExportAsync(GetDatabase(), effective, cancellationToken);
    }

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        => new TomModelMutator(GetDatabase()).AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        => new TomModelMutator(GetDatabase()).SetProperty(request);

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
        => new TomModelMutator(GetDatabase()).RemoveObject(request);

    public ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request)
        => new TomModelMutator(GetDatabase()).MoveObject(request);

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
        => new TomModelMutator(GetDatabase()).ReplaceText(request);

    public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        => new TomModelMutator(GetDatabase()).RewriteExpressions(edits);

    public RefreshPolicyInfo? GetRefreshPolicy(string table)
        => new TomRefreshPolicyManager(GetDatabase()).Get(table);

    public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request)
        => new TomRefreshPolicyManager(GetDatabase()).Set(request);

    public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false)
        => new TomRefreshPolicyManager(GetDatabase()).Remove(table, ifExists);

    public Task<ModelExportResult> SaveAsync(
        string? outputPath,
        string serialization,
        bool force,
        CancellationToken cancellationToken)
    {
        var inPlace = string.IsNullOrWhiteSpace(outputPath) || SamePath(outputPath, _path);
        var format = InPlaceSerializationGuard.Resolve(inPlace, serialization, sourceFormat: "tmdl");
        return TomModelExporter.ExportAsync(
            GetDatabase(),
            new ModelExportRequest(
                string.IsNullOrWhiteSpace(outputPath) ? _path : outputPath,
                format,
                Force: force || inPlace,
                SupportingFiles: false),
            cancellationToken);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public Task<ModelDeployResult> DeployAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken)
        => TomModelDeployer.DeployAsync(GetDatabase(), request, _tokenProvider, cancellationToken);

    public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken)
        => TomModelDeployer.GenerateScriptAsync(GetDatabase(), request, _tokenProvider, cancellationToken);

    private Database GetDatabase()
    {
        if (_database is not null)
            return _database;

        try
        {
            return _database = TmdlSerializer.DeserializeDatabaseFromFolder(_path);
        }
        catch (Exception ex)
        {
            throw new ModelLoadException($"Cannot load TMDL model from '{_path}': {ex.Message}", ex);
        }
    }

    private static string ModelName(Database database)
        => string.IsNullOrWhiteSpace(database.Name) ? "(unnamed)" : database.Name;
}
