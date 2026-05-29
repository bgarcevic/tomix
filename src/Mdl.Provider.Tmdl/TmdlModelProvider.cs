using Mdl.Core.Models;

namespace Mdl.Provider.Tmdl;

public sealed class TmdlModelProvider : IModelProvider
{
    public bool CanOpen(ModelReference reference)
    {
        if (!Directory.Exists(reference.Value))
            return false;

        return Directory.EnumerateFiles(reference.Value, "*.tmdl", SearchOption.AllDirectories).Any();
    }

    public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IModelSession>(new TmdlModelSession(reference.Value));
    }
}
