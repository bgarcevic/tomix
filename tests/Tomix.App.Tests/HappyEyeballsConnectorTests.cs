using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Tomix.Platform.Networking;

namespace Tomix.App.Tests;

/// <summary>
/// A network that advertises IPv6 it cannot route made <c>tx auth login</c> hang for minutes:
/// .NET tries addresses one at a time, IPv6 first, ~21s each. The connector races the families
/// (RFC 8305) so a hanging IPv6 address costs the attempt delay, not the whole request.
/// </summary>
public sealed class HappyEyeballsConnectorTests
{
    private static readonly IPAddress V6A = IPAddress.Parse("2a05:d018::1");
    private static readonly IPAddress V6B = IPAddress.Parse("2a05:d018::2");
    private static readonly IPAddress V4A = IPAddress.Parse("20.190.160.1");
    private static readonly IPAddress V4B = IPAddress.Parse("20.190.160.2");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private sealed class FakeNetwork
    {
        public ConcurrentQueue<IPAddress> Attempts { get; } = new();
        public ConcurrentBag<IPAddress> Cancelled { get; } = new();
        public Dictionary<IPAddress, Func<CancellationToken, Task<Stream>>> Behaviour { get; } = new();

        public async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            Attempts.Enqueue(endpoint.Address);
            try
            {
                return await Behaviour[endpoint.Address](cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.Add(endpoint.Address);
                throw;
            }
        }

        public static async Task<Stream> Hang(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public static Task<Stream> Connect(CancellationToken _) => Task.FromResult<Stream>(new MemoryStream());

        public static Task<Stream> Refuse(CancellationToken _)
            => Task.FromException<Stream>(new SocketException((int)SocketError.NetworkUnreachable));
    }

    private static HappyEyeballsConnector Connector(FakeNetwork network, TimeSpan delay, params IPAddress[] addresses)
        => new(
            resolve: (_, _) => Task.FromResult(addresses),
            connect: network.ConnectAsync,
            attemptDelay: delay,
            ipv6Supported: true);

    [Fact]
    public async Task HangingIpv6_FallsBackToIpv4_AndCancelsTheIpv6Attempt()
    {
        var network = new FakeNetwork();
        var ipv4Stream = new MemoryStream();
        network.Behaviour[V6A] = FakeNetwork.Hang;
        network.Behaviour[V4A] = _ => Task.FromResult<Stream>(ipv4Stream);

        var stream = await Connector(network, TimeSpan.FromMilliseconds(20), V6A, V4A)
            .ConnectAsync("login.microsoftonline.com", 443, CancellationToken.None)
            .WaitAsync(Guard);

        Assert.Same(ipv4Stream, stream);
        Assert.Equal([V6A, V4A], network.Attempts);
        Assert.Contains(V6A, network.Cancelled);
    }

    [Fact]
    public async Task Ipv6ThatConnects_Wins_AndIpv4IsNeverTried()
    {
        var network = new FakeNetwork();
        network.Behaviour[V6A] = FakeNetwork.Connect;
        network.Behaviour[V4A] = FakeNetwork.Connect;

        await Connector(network, TimeSpan.FromHours(1), V6A, V4A)
            .ConnectAsync("login.microsoftonline.com", 443, CancellationToken.None)
            .WaitAsync(Guard);

        Assert.Equal([V6A], network.Attempts);
    }

    [Fact]
    public async Task Ipv6ThatFailsFast_StartsIpv4WithoutWaitingForTheDelay()
    {
        var network = new FakeNetwork();
        network.Behaviour[V6A] = FakeNetwork.Refuse;
        network.Behaviour[V4A] = FakeNetwork.Connect;

        await Connector(network, TimeSpan.FromHours(1), V6A, V4A)
            .ConnectAsync("login.microsoftonline.com", 443, CancellationToken.None)
            .WaitAsync(Guard);

        Assert.Equal([V6A, V4A], network.Attempts);
    }

    [Fact]
    public async Task EveryAddressFailing_Throws()
    {
        var network = new FakeNetwork();
        network.Behaviour[V6A] = FakeNetwork.Refuse;
        network.Behaviour[V4A] = FakeNetwork.Refuse;

        await Assert.ThrowsAsync<SocketException>(() =>
            Connector(network, TimeSpan.FromMilliseconds(10), V6A, V4A)
                .ConnectAsync("login.microsoftonline.com", 443, CancellationToken.None)
                .WaitAsync(Guard));
        Assert.Equal(2, network.Attempts.Count);
    }

    [Fact]
    public async Task Cancellation_StopsEveryAttempt()
    {
        var network = new FakeNetwork();
        network.Behaviour[V6A] = FakeNetwork.Hang;
        network.Behaviour[V4A] = FakeNetwork.Hang;
        using var cancellation = new CancellationTokenSource();

        var connecting = Connector(network, TimeSpan.FromMilliseconds(10), V6A, V4A)
            .ConnectAsync("login.microsoftonline.com", 443, cancellation.Token);
        while (network.Attempts.Count < 2)
            await Task.Delay(5);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Guard));
        Assert.Equal(2, network.Cancelled.Count);
    }

    [Fact]
    public async Task IpLiteralHost_SkipsDns()
    {
        var network = new FakeNetwork();
        network.Behaviour[IPAddress.Loopback] = FakeNetwork.Connect;
        var connector = new HappyEyeballsConnector(
            resolve: (_, _) => throw new InvalidOperationException("DNS must not be used for an IP literal"),
            connect: network.ConnectAsync);

        await connector.ConnectAsync("127.0.0.1", 443, CancellationToken.None).WaitAsync(Guard);

        Assert.Equal([IPAddress.Loopback], network.Attempts);
    }

    [Fact]
    public void Order_InterleavesFamilies_StartingWithIpv6()
    {
        Assert.Equal(
            [V6A, V4A, V6B, V4B],
            HappyEyeballsConnector.Order([V4A, V4B, V6A, V6B], ipv6Supported: true));
    }

    [Fact]
    public void Order_DropsIpv6_WhenTheProcessCannotUseIt()
    {
        Assert.Equal([V4A, V4B], HappyEyeballsConnector.Order([V6A, V4A, V6B, V4B], ipv6Supported: false));
    }

    [Fact]
    public void CreateHandler_InstallsTheRacingConnectCallback()
    {
        using var handler = DualStackHttp.CreateHandler();

        Assert.NotNull(handler.ConnectCallback);
    }
}
