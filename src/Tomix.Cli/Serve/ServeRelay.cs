using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomix.Cli.Serve;

/// <summary>
/// <c>tx serve</c> on a model that <c>tx ui</c> already has open (#369): instead of a second session,
/// it passes its client's messages to the running one over its WebSocket, and the answers and events
/// back. The client is then one more client of the shared session, and leaving detaches it without
/// closing the session.
/// </summary>
internal static class ServeRelay
{
    /// <summary>Connects to the session <paramref name="entry"/> names, as a non-browser client.</summary>
    /// <exception cref="WebSocketException">The session cannot be reached.</exception>
    public static async Task<WebSocket> ConnectAsync(LiveEntry entry, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {entry.Token}");
        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{entry.Port}/ws"), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Relays until either side ends, and returns the exit code <c>tx serve</c> would: 0 after
    /// <c>shutdown</c> or when the client's input ends, 1 when <c>exit</c> came without <c>shutdown</c>.
    /// </summary>
    public static async Task<int> RunAsync(IMessageChannel client, IMessageChannel session, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lifecycle = new Lifecycle();
        var up = PumpAsync(client, session, lifecycle, stop.Token);
        var down = PumpAsync(session, client, lifecycle: null, stop.Token);
        if (await Task.WhenAny(up, down) == up)
        {
            // The client's input ended: the session answers what it was sent, then closes.
            await Quietly(up);
            await session.CompleteAsync();
            await Quietly(down);
        }
        else
        {
            // The session closed, after 'exit' or because tx ui stopped. The client's input may stay
            // open and a read on stdin may not cancel: as in ProtocolServer, it is not awaited.
            stop.Cancel();
            await Quietly(down);
            await session.CompleteAsync();
        }

        await client.CompleteAsync();
        return lifecycle.ExitCode;
    }

    private static async Task PumpAsync(IMessageChannel from, IMessageChannel to, Lifecycle? lifecycle, CancellationToken cancellationToken)
    {
        while (await from.ReadAsync(cancellationToken) is { } frame)
        {
            if (frame.Body is not { } body)
            {
                // A frame the client's side could not read never reaches the session: answer it here.
                if (lifecycle is not null)
                    from.Send(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new JsonObject { ["code"] = ProtocolErrors.InvalidRequest, ["message"] = frame.Problem }
                    }.ToJsonString());
                continue;
            }

            to.Send(Encoding.UTF8.GetString(body));
            lifecycle?.Saw(body);
        }
    }

    private static async Task Quietly(Task pump)
    {
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether the client has sent <c>shutdown</c> and <c>exit</c>.</summary>
    private sealed class Lifecycle
    {
        private volatile bool _shutdown;
        private volatile bool _exit;

        public int ExitCode => _exit && !_shutdown ? 1 : 0;

        public void Saw(byte[] body)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject message && message["method"] is JsonValue method)
                {
                    switch ((string?)method)
                    {
                        case "shutdown":
                            _shutdown = true;
                            break;
                        case "exit":
                            _exit = true;
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Not the relay's to judge: the session answers it.
            }
        }
    }
}
