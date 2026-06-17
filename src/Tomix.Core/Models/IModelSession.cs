namespace Tomix.Core.Models;

public interface IModelSession : IAsyncDisposable
{
    string SourcePath { get; }

    Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
