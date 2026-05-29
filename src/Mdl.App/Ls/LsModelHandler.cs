using Mdl.Core.Models;
using Mdl.Core.Results;

namespace Mdl.App.Ls;

public sealed class LsModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<MdlResult<LsModelResult>> HandleAsync(
        LsModelRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));

        if (provider is null)
            return MdlResult<LsModelResult>.Fail(
                code: "MDL_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 1);

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);
        var inventory = await session.GetInventoryAsync(cancellationToken);
        return MdlResult<LsModelResult>.Ok(new LsModelResult(inventory));
    }
}
