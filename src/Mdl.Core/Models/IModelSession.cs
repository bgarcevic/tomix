namespace Mdl.Core.Models;

public interface IModelSession : IAsyncDisposable
{
    Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken);
}
