using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tomix.Core.Authentication;

namespace Tomix.App.Connect;

/// <summary>
/// Lists workspaces via the Power BI REST API (<c>GET /v1.0/myorg/groups</c>). The token comes
/// from the same <see cref="IAccessTokenProvider"/> the XMLA providers use — the Power BI API
/// accepts the <c>analysis.windows.net/powerbi/api</c> audience, so no separate sign-in is needed.
/// </summary>
public sealed class PowerBiWorkspaceCatalog : IWorkspaceCatalog
{
    private static readonly Uri DefaultEndpoint = new("https://api.powerbi.com/v1.0/myorg/groups");
    private const int DefaultPageSize = 5000;

    // The scope for the REST API is derived from the endpoint the token provider is asked
    // for; any powerbi:// value maps to the fixed Power BI scope (see AuthScopes).
    private const string TokenEndpoint = "powerbi://api.powerbi.com/v1.0/myorg";

    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly Uri _endpoint;
    private readonly int _pageSize;

    public PowerBiWorkspaceCatalog(HttpClient httpClient, IAccessTokenProvider tokenProvider)
        : this(httpClient, tokenProvider, DefaultEndpoint)
    {
    }

    public PowerBiWorkspaceCatalog(HttpClient httpClient, IAccessTokenProvider tokenProvider, Uri endpoint)
        : this(httpClient, tokenProvider, endpoint, DefaultPageSize)
    {
    }

    internal PowerBiWorkspaceCatalog(HttpClient httpClient, IAccessTokenProvider tokenProvider, Uri endpoint, int pageSize)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _endpoint = endpoint;
        _pageSize = pageSize;
    }

    public async Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(TokenEndpoint, cancellationToken).ConfigureAwait(false);
        var workspaces = new List<WorkspaceInfo>();

        for (var skip = 0; ; skip += _pageSize)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{_endpoint}?%24top={_pageSize}&%24skip={skip}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient's Timeout surfaces as TaskCanceledException; without the invocation
                // token set, this is an endpoint timeout — an API failure, not a user interrupt
                // (which must keep propagating to the exit-130 path).
                throw new InvalidOperationException(
                    $"Power BI API request timed out after {_httpClient.Timeout.TotalSeconds:0} seconds listing workspaces.");
            }

            using var _ = response;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AuthenticationRequiredException(
                    "Not authenticated or no Power BI access. Run 'tx auth login'.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Power BI API returned HTTP {(int)response.StatusCode} listing workspaces: {Truncate(body)}");

            var page = ParsePage(body);
            workspaces.AddRange(page);
            if (page.Count < _pageSize)
                break;
        }

        return workspaces;
    }

    private static List<WorkspaceInfo> ParsePage(string body)
    {
        using var document = JsonDocument.Parse(body);
        var page = new List<WorkspaceInfo>();

        if (!document.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return page;

        foreach (var entry in value.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var id = entry.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? "" : "";
            var dedicated = entry.TryGetProperty("isOnDedicatedCapacity", out var capacityProperty) &&
                            capacityProperty.ValueKind == JsonValueKind.True;
            page.Add(new WorkspaceInfo(id, name, dedicated));
        }

        return page;
    }

    private static string Truncate(string body)
        => body.Length <= 200 ? body : body[..200] + "…";
}
