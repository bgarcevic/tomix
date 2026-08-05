using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.App.Connect;

/// <summary>
/// Lists workspaces (<c>GET /v1.0/myorg/groups</c>) and their semantic models
/// (<c>GET /v1.0/myorg/groups/{id}/datasets</c>) via the Power BI REST API. The token comes
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

    // ContentProviderTypes that are not tabular models on the XMLA endpoint — the set Power BI
    // documents as stored in the tenant home region rather than the capacity. A denylist, not an
    // allowlist: when Power BI adds a provider type, showing a model that turns out unopenable is a
    // smaller failure than silently hiding a real one.
    private static readonly HashSet<string> NonXmlaContentProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Excel",
        "CSV",
        "UsageMetricsUserReport",
        "UsageMetricsUserDashboard",
        "RealTimeInPushMode",
        "RealTimeInPubNubMode",
        "RealTimeInStreamingMode"
    };

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
            var body = await GetAsync(
                $"{_endpoint}?%24top={_pageSize}&%24skip={skip}", token.Token, "listing workspaces", cancellationToken)
                .ConfigureAwait(false);

            var page = ParsePage(body);
            workspaces.AddRange(page);
            if (page.Count < _pageSize)
                break;
        }

        return workspaces;
    }

    public async Task<IReadOnlyList<ServerDatabaseInfo>> ListDatasetsAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("A workspace id is required.", nameof(workspaceId));

        var token = await _tokenProvider.GetTokenAsync(TokenEndpoint, cancellationToken).ConfigureAwait(false);

        // Unlike /groups, the datasets endpoint ignores $top/$skip and returns the workspace in
        // one shot — so there is no paging loop here.
        var body = await GetAsync(
            $"{_endpoint}/{Uri.EscapeDataString(workspaceId)}/datasets",
            token.Token,
            "listing semantic models",
            cancellationToken).ConfigureAwait(false);

        return ParseDatasets(body);
    }

    /// <summary>
    /// One authenticated GET with the error contract both listings share: a client timeout, an
    /// auth rejection, and any other non-success status each map to a fixed exception, with
    /// <paramref name="operation"/> naming the listing in the message.
    /// </summary>
    private async Task<string> GetAsync(
        string uri,
        string token,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
                $"Power BI API request timed out after {_httpClient.Timeout.TotalSeconds:0} seconds {operation}.");
        }

        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AuthenticationRequiredException(
                "Not authenticated or no Power BI access. Run 'tx auth login'.");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Power BI API returned HTTP {(int)response.StatusCode} {operation}: {Truncate(body)}");

        return body;
    }

    private static List<ServerDatabaseInfo> ParseDatasets(string body)
    {
        using var document = JsonDocument.Parse(body);
        var datasets = new List<ServerDatabaseInfo>();

        if (!document.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return datasets;

        foreach (var entry in value.EnumerateArray())
        {
            var name = TryGetProperty(entry, "name", out var nameProperty) ? nameProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (IsNotAddressableOverXmla(entry))
                continue;

            // Compatibility level and last-update are XMLA-only facts the REST API does not
            // report; no caller reads them, so they stay null rather than forcing a second call.
            datasets.Add(new ServerDatabaseInfo(name));
        }

        return datasets;
    }

    /// <summary>
    /// Whether the entry is something the XMLA endpoint cannot serve — a push/streaming dataset, an
    /// Excel or CSV upload, or a usage-metrics model. Offering one would only fail later when the
    /// connection is opened, and the XMLA listing this replaced never showed them.
    /// <para>
    /// This can only be judged when the response carries the metadata. Power BI returns full dataset
    /// content only to callers with Write permission; a Read-only caller gets just <c>id</c> and
    /// <c>name</c> (documented under "Limitations" on Get Datasets In Group), so for those entries
    /// nothing here can fire and an unopenable model may still be offered. That is deliberate: the
    /// alternative — falling back to the XMLA listing whenever metadata is thin — would forfeit the
    /// whole speedup for exactly the read-only, inspect-only caller. The residual case surfaces as
    /// the normal "database not found on endpoint" diagnostic when the model is opened.
    /// </para>
    /// </summary>
    private static bool IsNotAddressableOverXmla(JsonElement entry)
    {
        if (TryGetProperty(entry, "addRowsAPIEnabled", out var push) && push.ValueKind == JsonValueKind.True)
            return true;

        return TryGetProperty(entry, "contentProviderType", out var provider) &&
               provider.ValueKind == JsonValueKind.String &&
               NonXmlaContentProviders.Contains(provider.GetString()!);
    }

    /// <summary>
    /// Case-insensitive property lookup. The REST reference spells <c>ContentProviderType</c> with a
    /// leading capital while every neighbouring property is camelCase, so the documented casing is
    /// not worth betting on for a filter that fails open.
    /// </summary>
    private static bool TryGetProperty(JsonElement entry, string name, out JsonElement value)
    {
        if (entry.TryGetProperty(name, out value))
            return true;

        foreach (var property in entry.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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
