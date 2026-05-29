namespace Mdl.Core.Models;

public interface IModelProvider
{
    bool CanOpen(ModelReference reference);

    Task<IModelSession> OpenAsync(
        ModelReference reference,
        CancellationToken cancellationToken);
}
