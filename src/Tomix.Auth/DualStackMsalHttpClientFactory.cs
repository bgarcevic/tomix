using Microsoft.Identity.Client;
using Tomix.Platform.Networking;

namespace Tomix.Auth;

/// <summary>
/// Gives MSAL an HTTP client that races IPv6 and IPv4. MSAL's own client tries one address at a
/// time, so on a network that advertises IPv6 it cannot route, sign-in timed out before it ever
/// reached IPv4. One client for the process, as MSAL recommends.
/// </summary>
internal sealed class DualStackMsalHttpClientFactory : IMsalHttpClientFactory
{
    private static readonly HttpClient Client = new(DualStackHttp.CreateHandler());

    public static DualStackMsalHttpClientFactory Instance { get; } = new();

    public HttpClient GetHttpClient() => Client;
}
