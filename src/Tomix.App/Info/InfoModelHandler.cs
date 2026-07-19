using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Info;

public sealed class InfoModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public InfoModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<InfoModelResult>> HandleAsync(
        InfoModelRequest request,
        CancellationToken cancellationToken)
    {
        return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
        {
            var summary = await session.GetSummaryAsync(cancellationToken);
            return TomixResult<InfoModelResult>.Ok(new InfoModelResult(summary));
        }, cancellationToken);
    }
}
