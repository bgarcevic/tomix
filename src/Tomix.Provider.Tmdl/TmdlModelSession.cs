using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tmdl;

public sealed class TmdlModelSession : IModelSession, IModelExportSession, IModelMutationSession, IModelDeploySession
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
            with { DatabaseName = string.IsNullOrWhiteSpace(database.Name) ? null : database.Name });
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
        => TomModelExporter.ExportAsync(GetDatabase(), request, cancellationToken);

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        => new TomModelMutator(GetDatabase()).AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        => new TomModelMutator(GetDatabase()).SetProperty(request);

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
        => new TomModelMutator(GetDatabase()).RemoveObject(request);

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
        => new TomModelMutator(GetDatabase()).ReplaceText(request);

    public Task<ModelExportResult> SaveAsync(
        string? outputPath,
        string serialization,
        bool force,
        CancellationToken cancellationToken)
        => TomModelExporter.ExportAsync(
            GetDatabase(),
            new ModelExportRequest(
                string.IsNullOrWhiteSpace(outputPath) ? _path : outputPath,
                string.IsNullOrWhiteSpace(serialization) ? "tmdl" : serialization,
                Force: force,
                SupportingFiles: false),
            cancellationToken);

    public Task<ModelDeployResult> DeployAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken)
        => TomModelDeployer.DeployAsync(GetDatabase(), request, _tokenProvider, cancellationToken);

    public string GenerateScript(ModelDeployRequest request)
        => TomModelDeployer.GenerateScript(GetDatabase(), request);

    private Database GetDatabase() => _database ??= TmdlSerializer.DeserializeDatabaseFromFolder(_path);

    private static string ModelName(Database database)
        => string.IsNullOrWhiteSpace(database.Name) ? "(unnamed)" : database.Name;
}
