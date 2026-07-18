using Tomix.App.Diagnostics;
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
        var provider = _providers.ResolveSingle(request.Model);

        if (provider is null)
            return TomixResult<InfoModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            await using var session = await provider.OpenAsync(request.Model, cancellationToken);
            var summary = await session.GetSummaryAsync(cancellationToken);
            return TomixResult<InfoModelResult>.Ok(new InfoModelResult(summary));
        });
    }
}
