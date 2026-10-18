using System.Net;
using System.Net.Sockets;

namespace Tomix.Platform.Networking;

/// <summary>Resolves a host name to its addresses (both families).</summary>
public delegate Task<IPAddress[]> ResolveAddresses(string host, CancellationToken cancellationToken);

/// <summary>Opens one TCP connection to one address.</summary>
public delegate Task<Stream> ConnectAttempt(IPEndPoint endpoint, CancellationToken cancellationToken);

/// <summary>
/// Connects the way browsers do (Happy Eyeballs, RFC 8305): IPv6 and IPv4 addresses are
/// interleaved, IPv6 first, and each next attempt starts when the previous one fails or after
/// <see cref="DefaultAttemptDelay"/>, whichever comes first. The first connection wins and the
/// rest are cancelled.
/// <para>
/// .NET's default tries addresses one at a time, waiting ~21s on each, so a network that
/// advertises IPv6 it cannot route (a router handing out global addresses with no IPv6 upstream)
/// stalls every request until the caller's timeout instead of falling back to IPv4.
/// </para>
/// </summary>
public sealed class HappyEyeballsConnector
{
    /// <summary>The "connection attempt delay" RFC 8305 recommends.</summary>
    public static readonly TimeSpan DefaultAttemptDelay = TimeSpan.FromMilliseconds(250);

    private readonly ResolveAddresses _resolve;
    private readonly ConnectAttempt _connect;
    private readonly TimeSpan _attemptDelay;
    private readonly bool _ipv6Supported;

    /// <param name="resolve">Name resolution; defaults to DNS.</param>
    /// <param name="connect">One connection attempt; defaults to a TCP socket.</param>
    /// <param name="attemptDelay">How long an attempt runs before the next one starts beside it.</param>
    /// <param name="ipv6Supported">
    /// Whether the process may use IPv6; defaults to <see cref="Socket.OSSupportsIPv6"/>, which
    /// <c>DOTNET_SYSTEM_NET_DISABLEIPV6=1</c> turns off.
    /// </param>
    public HappyEyeballsConnector(
        ResolveAddresses? resolve = null,
        ConnectAttempt? connect = null,
        TimeSpan? attemptDelay = null,
        bool? ipv6Supported = null)
    {
        _resolve = resolve ?? ResolveWithDnsAsync;
        _connect = connect ?? ConnectSocketAsync;
        _attemptDelay = attemptDelay ?? DefaultAttemptDelay;
        _ipv6Supported = ipv6Supported ?? Socket.OSSupportsIPv6;
    }

    public static HappyEyeballsConnector Default { get; } = new();

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await _resolve(host, cancellationToken).ConfigureAwait(false);
        var ordered = Order(addresses, _ipv6Supported);
        if (ordered.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task<Stream>>();
        Exception? firstError = null;
        Task? delay = null;
        var next = 0;
        var startNext = true;
        try
        {
            while (true)
            {
                if (startNext && next < ordered.Count)
                {
                    running.Add(_connect(new IPEndPoint(ordered[next++], port), race.Token));
                    delay = next < ordered.Count ? Task.Delay(_attemptDelay, race.Token) : null;
                }
                startNext = false;

                if (running.Count == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw firstError ?? new SocketException((int)SocketError.HostUnreachable);
                }

                var completed = await Task.WhenAny(delay is null ? running : [.. running, delay]).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == delay)
                {
                    delay = null;
                    startNext = true;
                    continue;
                }

                var attempt = (Task<Stream>)completed;
                running.Remove(attempt);
                if (attempt.Status == TaskStatus.RanToCompletion)
                    return attempt.Result;

                firstError ??= attempt.Exception?.InnerException ?? new OperationCanceledException();
                startNext = true;
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            await AbandonAsync(running).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Interleaves the address families, IPv6 first, keeping each family's DNS order
    /// (RFC 8305 section 4); drops IPv6 when the process cannot use it.
    /// </summary>
    public static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> addresses, bool ipv6Supported)
    {
        var list = addresses.ToList();
        var ipv6 = ipv6Supported
            ? list.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6).ToList()
            : [];
        var ipv4 = list.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToList();

        var ordered = new List<IPAddress>(ipv6.Count + ipv4.Count);
        for (var i = 0; i < Math.Max(ipv6.Count, ipv4.Count); i++)
        {
            if (i < ipv6.Count)
                ordered.Add(ipv6[i]);
            if (i < ipv4.Count)
                ordered.Add(ipv4[i]);
        }
        return ordered;
    }

    /// <summary>Waits for the losing attempts to observe cancellation and closes any that connected anyway.</summary>
    private static async Task AbandonAsync(List<Task<Stream>> attempts)
    {
        foreach (var attempt in attempts)
        {
            try
            {
                var stream = await attempt.ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A losing attempt's failure (usually its cancellation) is not the caller's concern.
            }
        }
    }

    internal static Task<IPAddress[]> ResolveWithDnsAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    internal static async Task<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
