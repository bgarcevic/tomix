namespace Tomix.Platform.Networking;

/// <summary>
/// HTTP handlers whose connections race IPv6 and IPv4 (<see cref="HappyEyeballsConnector"/>),
/// for every <see cref="HttpClient"/> tx builds itself: sign-in and the REST calls. Proxies
/// still apply; the race then runs against the proxy's addresses.
/// </summary>
public static class DualStackHttp
{
    public static SocketsHttpHandler CreateHandler(HappyEyeballsConnector? connector = null)
    {
        var dialer = connector ?? HappyEyeballsConnector.Default;
        return new SocketsHttpHandler
        {
            ConnectCallback = (context, cancellationToken) =>
                new ValueTask<Stream>(dialer.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken)),
            // Pick up DNS changes (and a network that starts or stops routing IPv6) in long sessions.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
    }
}
