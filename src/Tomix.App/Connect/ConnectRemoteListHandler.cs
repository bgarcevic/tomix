using Tomix.App.Diagnostics;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Connect;

/// <summary>
/// <c>tx connect &lt;server&gt; --list</c>: enumerates the semantic models (databases) on a remote
/// XMLA endpoint without connecting to one or touching the saved session — the non-interactive
/// counterpart of the model picker, for scripts and agents. The server may be a bare workspace
/// name; it is normalized to an endpoint the same way <c>tx connect</c> does.
/// </summary>
public sealed class ConnectRemoteListHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ConnectRemoteListHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<ConnectRemoteListResult>> HandleAsync(
        string server,
        CancellationToken cancellationToken)
    {
        var endpoint = ModelReference.NormalizeEndpoint(server.Trim());
        var reference = ModelReference.Remote(endpoint);
        if (!reference.IsRemote)
            return TomixResult<ConnectRemoteListResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"Not a remote endpoint: {server}",
                exitCode: 2,
                hint: "Pass a workspace name or an XMLA endpoint (powerbi://, asazure://, localhost:<port>).");

        var catalog = _providers.OfType<IServerCatalog>().FirstOrDefault(c => c.CanList(reference));
        if (catalog is null)
            return TomixResult<ConnectRemoteListResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can list models on '{endpoint}'.",
                exitCode: 2);

        try
        {
            var databases = await catalog.ListDatabasesAsync(reference, cancellationToken).ConfigureAwait(false);
            var models = databases
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return TomixResult<ConnectRemoteListResult>.Ok(new ConnectRemoteListResult(endpoint, models));
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<ConnectRemoteListResult>.Fail(
                "TOMIX_AUTH_REQUIRED",
                ex.Message,
                exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TomixResult<ConnectRemoteListResult>.Fail(
                "TOMIX_REMOTE_LIST_FAILED",
                RemoteConnectError.Describe(endpoint, ex),
                exitCode: 1,
                hint: "Verify the workspace name or endpoint and that your account can access it.");
        }
    }
}

/// <param name="Server">The normalized XMLA endpoint that was listed; pass it to <c>tx connect</c>.</param>
/// <param name="Models">The databases on the endpoint, sorted by name.</param>
public sealed record ConnectRemoteListResult(string Server, IReadOnlyList<ServerDatabaseInfo> Models);
