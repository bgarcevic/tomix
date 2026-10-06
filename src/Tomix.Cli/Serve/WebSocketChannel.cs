using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Tomix.Cli.Serve;

/// <summary>
/// Protocol messages over a WebSocket (docs/protocol.md, Transport): one JSON-RPC message per text
/// message, no <c>Content-Length</c> headers. A binary message is answered as a bad frame.
/// </summary>
internal sealed class WebSocketChannel : IMessageChannel
{
    private readonly WebSocket _socket;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public WebSocketChannel(WebSocket socket)
    {
        _socket = socket;
        _pump = Task.Run(PumpAsync);
    }

    public async Task<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                    return null;

                if (message.Length + received.Count > FrameReader.MaxBodyBytes)
                    return await SkipAsync(buffer, received, cancellationToken)
                        ? new ProtocolFrame(null, $"The message is over the limit of {FrameReader.MaxBodyBytes} bytes.")
                        : null;

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;

                return received.MessageType == WebSocketMessageType.Text
                    ? new ProtocolFrame(message.ToArray(), null)
                    : new ProtocolFrame(null, "Binary messages are not part of the protocol; send each message as text.");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            // The other side went away without closing.
            return null;
        }
    }

    public void Send(string json) => _queue.Writer.TryWrite(json);

    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                // The other side has already gone.
            }
        }
    }

    /// <summary>Reads to the end of an oversized message; false when the client went away meanwhile.</summary>
    private async Task<bool> SkipAsync(byte[] buffer, WebSocketReceiveResult received, CancellationToken cancellationToken)
    {
        while (!received.EndOfMessage)
        {
            received = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return false;
        }

        return true;
    }

    private async Task PumpAsync()
    {
        await foreach (var json in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
                // The client went away; the reader sees it and the connection ends.
            }
        }
    }
}
