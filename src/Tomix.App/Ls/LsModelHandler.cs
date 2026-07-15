using Tomix.App.Diagnostics;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Ls;

public sealed class LsModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<LsModelResult>> HandleAsync(
        LsModelRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));

        if (provider is null)
            return TomixResult<LsModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        try
        {
            await using var session = await provider.OpenAsync(request.Model, cancellationToken);
            var snapshot = await session.GetSnapshotAsync(cancellationToken);

            var matches = ModelObjectSelector
                .Select(snapshot, request.PathFilter, request.Type)
                .Select(o => new LsObject(
                    o.Path, o.Name, o.Kind, o.Detail, o.Expression, o.Description, o.Hidden,
                    o.SourceColumn,
                    o.Children.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    ModelPropertyCatalog.Project(o)))
                .ToList();

            return TomixResult<LsModelResult>.Ok(
                new LsModelResult(snapshot.Name, snapshot.CompatibilityLevel, matches));
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<LsModelResult>.Fail("TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (Exception ex) when (request.Model.IsRemote && ex is not OperationCanceledException)
        {
            return TomixResult<LsModelResult>.Fail(
                "TOMIX_CONNECT_FAILED",
                RemoteConnectError.Describe(request.Model.Value, ex),
                exitCode: 1,
                hint: "Verify the server URL and credentials.");
        }
    }
}
