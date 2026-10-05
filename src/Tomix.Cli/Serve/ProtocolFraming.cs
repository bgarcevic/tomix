using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Tomix.Cli.Serve;

/// <summary>One frame read from the client: its body, or why the header block was unusable.</summary>
internal sealed record ProtocolFrame(byte[]? Body, string? Problem);

/// <summary>
/// How protocol messages travel: <c>Content-Length</c> frames on stdio (<see cref="StreamChannel"/>)
/// or one message per WebSocket text message (<see cref="WebSocketChannel"/>).
/// </summary>
internal interface IMessageChannel
{
    /// <summary>The next message, or <c>null</c> when the client has gone.</summary>
    Task<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Queues a message for the client; never blocks.</summary>
    void Send(string json);

    /// <summary>Writes what is queued, then stops sending.</summary>
    Task CompleteAsync();
}

/// <summary>Frames over a pair of streams: stdin and stdout for <c>tx serve</c>.</summary>
internal sealed class StreamChannel(Stream input, Stream output) : IMessageChannel
{
    private readonly FrameReader _reader = new(input);
    private readonly FrameWriter _writer = new(output);

    public Task<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken) => _reader.ReadAsync(cancellationToken);

    public void Send(string json) => _writer.Send(json);

    public Task CompleteAsync() => _writer.CompleteAsync();
}

/// <summary>
/// Reads Language Server Protocol frames (docs/protocol.md, Transport): header lines ending in a
/// blank line, of which only <c>Content-Length</c> matters, then exactly that many bytes of UTF-8
/// JSON. A header block without a usable length is reported as a <see cref="ProtocolFrame.Problem"/>
/// and reading goes on with the next block, so one bad frame does not end the connection.
/// </summary>
internal sealed class FrameReader(Stream input)
{
    /// <summary>Bodies above this are refused (and skipped) rather than buffered.</summary>
    internal const int MaxBodyBytes = 64 * 1024 * 1024;

    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <summary>The next frame, or <c>null</c> at the end of input (including input that ends mid-frame).</summary>
    public async Task<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        int? length = null;
        string? problem = null;
        var sawHeader = false;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null)
                return null;
            if (line.Length == 0)
            {
                // Blank lines between frames are tolerated; a blank line after headers ends them.
                if (!sawHeader)
                    continue;
                break;
            }

            sawHeader = true;
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                problem ??= $"Malformed header line '{line}'.";
                continue;
            }

            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                length = value;
            else
                problem ??= $"Invalid Content-Length '{line[(colon + 1)..].Trim()}'.";
        }

        if (problem is not null || length is null)
        {
            if (length is { } skip)
                await SkipAsync(skip, cancellationToken);
            return new ProtocolFrame(null, problem ?? "The frame has no Content-Length header.");
        }

        if (length > MaxBodyBytes)
        {
            await SkipAsync(length.Value, cancellationToken);
            return new ProtocolFrame(null, $"The frame body is {length} bytes; the limit is {MaxBodyBytes}.");
        }

        var body = new byte[length.Value];
        var read = 0;
        while (read < body.Length)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
                return null;
            var count = Math.Min(_end - _start, body.Length - read);
            Array.Copy(_buffer, _start, body, read, count);
            _start += count;
            read += count;
        }

        return new ProtocolFrame(body, null);
    }

    /// <summary>One header line without its line ending (CRLF, or a bare LF), or <c>null</c> at the end of input.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
                return null;

            var b = _buffer[_start++];
            if (b == (byte)'\n')
            {
                if (line.Count > 0 && line[^1] == (byte)'\r')
                    line.RemoveAt(line.Count - 1);
                return Encoding.ASCII.GetString([.. line]);
            }

            line.Add(b);
        }
    }

    private async Task SkipAsync(int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
                return;
            var skipped = Math.Min(_end - _start, count);
            _start += skipped;
            count -= skipped;
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await input.ReadAsync(_buffer, cancellationToken);
        return _end > 0;
    }
}

/// <summary>
/// Writes frames to the client in the order they are sent. <see cref="Send"/> never blocks, so a
/// session event raised on the committing thread is queued behind the answers before it.
/// </summary>
internal sealed class FrameWriter
{
    private readonly Stream _output;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public FrameWriter(Stream output)
    {
        _output = output;
        _pump = Task.Run(PumpAsync);
    }

    public void Send(string json) => _queue.Writer.TryWrite(json);

    /// <summary>Writes what is queued, then stops.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
    }

    private async Task PumpAsync()
    {
        await foreach (var json in _queue.Reader.ReadAllAsync())
        {
            var body = Encoding.UTF8.GetBytes(json);
            try
            {
                await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
                await _output.WriteAsync(body);
                await _output.FlushAsync();
            }
            catch (IOException)
            {
                // The client went away; the reader sees the end of input and the server stops.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
