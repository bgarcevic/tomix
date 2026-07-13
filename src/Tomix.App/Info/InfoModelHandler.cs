using Tomix.App.Diagnostics;
using Tomix.Core.Authentication;
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
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));

        if (provider is null)
            return TomixResult<InfoModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        try
        {
            await using var session = await provider.OpenAsync(request.Model, cancellationToken);
            var summary = await session.GetSummaryAsync(cancellationToken);
            return TomixResult<InfoModelResult>.Ok(new InfoModelResult(summary));
        }
        catch (InvalidOperationException ex)
            when (request.Model.IsRemote && ex.Message.Contains("Database not found on endpoint"))
        {
            return TomixResult<InfoModelResult>.Fail("TOMIX_DATABASE_NOT_FOUND", ex.Message, exitCode: 1);
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<InfoModelResult>.Fail("TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (Exception ex) when (request.Model.IsRemote && ex is not OperationCanceledException)
        {
            return TomixResult<InfoModelResult>.Fail(
                "TOMIX_CONNECT_FAILED",
                RemoteConnectError.Describe(request.Model.Value, ex),
                exitCode: 1,
                hint: "Verify the server URL and credentials.");
        }
    }
}
