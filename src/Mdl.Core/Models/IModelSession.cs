namespace Mdl.Core.Models;

public interface IModelSession : IAsyncDisposable
{
    Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
