using Microsoft.AnalysisServices.Tabular;
using Mdl.Core.Models;
using Mdl.Provider.Tom;

namespace Mdl.Provider.Tmdl;

public sealed class TmdlModelSession : IModelSession
{
    private readonly string _path;
    private Database? _database;

    public TmdlModelSession(string path) => _path = path;

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = GetDatabase();
        return Task.FromResult(TomModelSummarizer.Summarize(database, Path.GetFileName(_path)));
    }

    public Task<ModelInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = GetDatabase();
        return Task.FromResult(TomModelSummarizer.Inventory(database, Path.GetFileName(_path)));
    }

    public ValueTask DisposeAsync()
    {
        _database = null;
        return ValueTask.CompletedTask;
    }

    private Database GetDatabase() => _database ??= TmdlSerializer.DeserializeDatabaseFromFolder(_path);
}
