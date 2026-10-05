using Microsoft.AspNetCore.Hosting;
using Tomix.Ui;

namespace Tomix.Cli.Serve;

/// <summary>
/// Puts a <see cref="SessionHost"/> on localhost: each WebSocket on <c>/ws</c> is one more
/// protocol client of the shared session, and <c>GET /status</c> answers the host's status.
/// </summary>
internal static class WebEndpoint
{
    /// <param name="configure">Replaces the server, for tests.</param>
    public static Task<UiHost> StartAsync(
        SessionHost host,
        string serverVersion,
        int port,
        string token,
        CancellationToken cancellationToken,
        Action<IWebHostBuilder>? configure = null)
        => UiHost.StartAsync(
            new UiHostOptions
            {
                Port = port,
                Token = token,
                Status = () => host.Status,
                Connect = (socket, aborted) =>
                    new ProtocolServer(new WebSocketChannel(socket), host.Connect(), serverVersion, host.Log).RunAsync(aborted)
            },
            configure,
            cancellationToken);
}
