using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Info;

public sealed class InfoModelHandler
{
    private readonly IModelSessionSource _sessions;

    public InfoModelHandler(IEnumerable<IModelProvider> providers)
        : this(new OneShotSessionSource(providers))
    {
    }

    public InfoModelHandler(IModelSessionSource sessions)
        => _sessions = sessions;

    public async Task<TomixResult<InfoModelResult>> HandleAsync(
        InfoModelRequest request,
        CancellationToken cancellationToken)
    {
        return await ModelSessionRunner.RunAsync(_sessions, request.Model, async session =>
        {
            var summary = await session.GetSummaryAsync(cancellationToken);
            return TomixResult<InfoModelResult>.Ok(new InfoModelResult(summary));
        }, cancellationToken);
    }
}
