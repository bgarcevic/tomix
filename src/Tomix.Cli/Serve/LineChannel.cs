using System.Text;
using System.Threading.Channels;

namespace Tomix.Cli.Serve;

/// <summary>
/// Messages over a pair of streams as MCP's stdio transport carries them: one JSON-RPC message per
/// line of UTF-8, with no embedded newlines and no headers. Blank lines are skipped, and so is a
/// byte order mark at the start, which Windows PowerShell puts in front of what it pipes.
/// </summary>
internal sealed class LineChannel : IMessageChannel, IDisposable
{
    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public LineChannel(Stream input, Stream output)
    {
        _reader = new StreamReader(input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        _output = output;
        _pump = Task.Run(PumpAsync);
    }

    public async Task<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
                continue;
            if (Encoding.UTF8.GetByteCount(line) > FrameReader.MaxBodyBytes)
                return new ProtocolFrame(null, $"The message is over the limit of {FrameReader.MaxBodyBytes} bytes.");
            return new ProtocolFrame(Encoding.UTF8.GetBytes(line), null);
        }

        return null;
    }

    /// <summary>Queues <paramref name="json"/>, which must not contain a newline, as one line.</summary>
    public void Send(string json) => _queue.Writer.TryWrite(json);

    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
    }

    public void Dispose() => _reader.Dispose();

    private async Task PumpAsync()
    {
        await foreach (var json in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _output.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"));
                await _output.FlushAsync();
            }
            catch (IOException)
            {
                // The client went away; the reader sees the end of input.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
