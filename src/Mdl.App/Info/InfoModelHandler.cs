using Mdl.Core.Models;
using Mdl.Core.Results;

namespace Mdl.App.Info;

public sealed class InfoModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public InfoModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<MdlResult<InfoModelResult>> HandleAsync(
        InfoModelRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));

        if (provider is null)
            return MdlResult<InfoModelResult>.Fail(
                code: "MDL_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 1);

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);
        var summary = await session.GetSummaryAsync(cancellationToken);
        return MdlResult<InfoModelResult>.Ok(new InfoModelResult(summary));
    }
}
