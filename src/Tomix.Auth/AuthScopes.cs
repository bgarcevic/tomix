namespace Tomix.Auth;

/// <summary>
/// Maps an XMLA endpoint to the OAuth scope/resource needed to obtain a token for it.
/// Power BI uses a fixed scope; Azure AS is region-specific (MSAL rejects wildcard scopes).
/// </summary>
internal static class AuthScopes
{
    public const string PowerBi = "https://analysis.windows.net/powerbi/api/.default";

    public static string[] ForEndpoint(string? endpoint) => [ScopeForEndpoint(endpoint)];

    public static string ScopeForEndpoint(string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            endpoint.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase))
        {
            var host = HostOf(endpoint, "asazure://");
            if (!string.IsNullOrEmpty(host))
                return $"https://{host}/.default";
        }

        return PowerBi;
    }

    /// <summary>The bare resource (no <c>/.default</c> suffix), used by the managed-identity flow.</summary>
    public static string ResourceForEndpoint(string? endpoint)
    {
        var scope = ScopeForEndpoint(endpoint);
        return scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
            ? scope[..^"/.default".Length]
            : scope;
    }

    private static string? HostOf(string endpoint, string scheme)
    {
        var rest = endpoint[scheme.Length..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }
}
