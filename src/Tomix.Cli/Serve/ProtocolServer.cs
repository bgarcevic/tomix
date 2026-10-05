using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Tomix.Cli.Serve;

/// <summary>What a protocol server offers beyond the lifecycle: the session methods of <c>tx serve</c>.</summary>
internal interface IProtocolMethods
{
    /// <summary>The request methods <see cref="InvokeAsync"/> answers, as <c>initialize</c> lists them.</summary>
    IReadOnlyList<string> Methods { get; }

    /// <summary>The notifications the server sends.</summary>
    IReadOnlyList<string> Notifications { get; }

    /// <summary>Called once <c>initialize</c> has named the client, before any other request.</summary>
    /// <param name="notify">Sends a notification to the client; never blocks.</param>
    void Attach(string clientId, Action<string, JsonNode?> notify);

    /// <summary>Answers one request. Throws <see cref="ProtocolException"/> to answer with an error.</summary>
    Task<JsonNode?> InvokeAsync(string method, JsonObject parameters, CancellationToken cancellationToken);

    /// <summary>The client has gone (<c>exit</c> or the end of input): release what it held.</summary>
    ValueTask DetachAsync();
}

/// <summary>
/// The JSON-RPC 2.0 side of <c>tx serve</c> (docs/protocol.md): framing, the
/// <c>initialize</c>/<c>shutdown</c>/<c>exit</c> lifecycle, request validation, cancellation and
/// error answers. Requests run one at a time in arrival order; a reader keeps reading meanwhile, so
/// <c>$/cancelRequest</c> reaches a request that is queued or running.
/// </summary>
internal sealed class ProtocolServer
{
    public const string ProtocolVersion = "0";

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly IProtocolMethods _methods;
    private readonly string _serverVersion;
    private readonly TextWriter _log;
    private readonly Channel<JsonObject> _queue = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _shutdown;
    private int? _exitCode;

    public ProtocolServer(Stream input, Stream output, IProtocolMethods methods, string serverVersion, TextWriter log)
    {
        _reader = new FrameReader(input);
        _writer = new FrameWriter(output);
        _methods = methods;
        _serverVersion = serverVersion;
        _log = log;
    }

    /// <summary>
    /// Serves until <c>exit</c> or the end of input, and returns the exit code: 0 after
    /// <c>shutdown</c> or at the end of input, 1 when <c>exit</c> came without <c>shutdown</c>.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var reading = Task.Run(() => ReadAsync(cancellationToken), CancellationToken.None);
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(CancellationToken.None))
            {
                await HandleAsync(message);
                if (_exitCode is not null)
                    break;
            }
        }
        finally
        {
            await _methods.DetachAsync();
            await _writer.CompleteAsync();
        }

        // After 'exit' the reader may still be waiting on input that never comes; it is not awaited.
        if (_exitCode is null)
            await reading;
        return _exitCode ?? 0;
    }

    /// <summary>Reads frames until the end of input. Cancellations act at once; everything else
    /// is queued in order.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _reader.ReadAsync(cancellationToken) is { } frame)
            {
                if (frame.Problem is not null)
                {
                    Log($"bad frame: {frame.Problem}");
                    SendError(null, ProtocolErrors.InvalidRequest, frame.Problem);
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(frame.Body);
                }
                catch (JsonException ex)
                {
                    SendError(null, ProtocolErrors.ParseError, $"The body is not valid JSON: {ex.Message}");
                    continue;
                }

                if (node is JsonArray)
                {
                    SendError(null, ProtocolErrors.InvalidRequest, "Batch requests are not supported in protocol v0.");
                    continue;
                }

                if (node is not JsonObject message)
                {
                    SendError(null, ProtocolErrors.InvalidRequest, "A message must be a JSON object.");
                    continue;
                }

                if (Validate(message) is { } problem)
                {
                    SendError(ValidId(message), ProtocolErrors.InvalidRequest, problem);
                    continue;
                }

                if (!message.ContainsKey("method"))
                {
                    // An answer from the client: the server sends no requests, so there is nothing to match.
                    Log("ignored a response from the client");
                    continue;
                }

                var method = (string)message["method"]!;
                if (method == "$/cancelRequest" && !message.ContainsKey("id"))
                {
                    Cancel(message["params"]);
                    continue;
                }

                if (message.ContainsKey("id"))
                    _pending[Key(message["id"])] = new CancellationTokenSource();
                await _queue.Writer.WriteAsync(message, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client closed the stream: the same as the end of input.
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var method = (string)message["method"]!;
        if (!message.ContainsKey("id"))
        {
            HandleNotification(method);
            return;
        }

        var id = message["id"];
        var key = Key(id);
        var cancellation = _pending.GetValueOrDefault(key) ?? new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await DispatchAsync(method, message["params"], cancellation.Token);
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });
            Log($"{method} #{key} ok ({clock.ElapsedMilliseconds} ms)");
        }
        catch (ProtocolException ex)
        {
            SendError(id, ex.Code, ex.Message, ex.ErrorData);
            Log($"{method} #{key} error {ex.Code}{(ex.ErrorData?["code"] is { } code ? $" {code}" : "")} ({clock.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SendError(id, ProtocolErrors.RequestCancelled, "The request was cancelled.");
            Log($"{method} #{key} cancelled");
        }
        catch (Exception ex)
        {
            SendError(id, ProtocolErrors.InternalError, $"Unexpected error: {ex.Message}", new JsonObject { ["code"] = "TOMIX_UNEXPECTED" });
            Log($"{method} #{key} failed: {ex}");
        }
        finally
        {
            _pending.TryRemove(key, out _);
            cancellation.Dispose();
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        if (parameters is not null and not JsonObject)
            throw ProtocolException.InvalidParams("params must be an object.");
        var arguments = parameters as JsonObject ?? [];

        if (method == "initialize")
            return Initialize(arguments);
        if (!_initialized)
            throw new ProtocolException(ProtocolErrors.NotInitialized, "The server is not initialized; send 'initialize' first.");
        if (_shutdown)
            throw new ProtocolException(ProtocolErrors.InvalidRequest, "The server is shutting down; only 'exit' is accepted.");
        if (method == "shutdown")
        {
            _shutdown = true;
            return null;
        }

        if (!_methods.Methods.Contains(method))
            throw new ProtocolException(ProtocolErrors.MethodNotFound, $"Unknown method '{method}'.");
        return await _methods.InvokeAsync(method, arguments, cancellationToken);
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        if (_initialized)
            throw new ProtocolException(ProtocolErrors.InvalidRequest, "The server is already initialized.");

        var requested = parameters["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (requested is not null && requested != ProtocolVersion)
        {
            var failure = ProtocolException.Tomix(
                "TOMIX_PROTOCOL_VERSION",
                $"This server speaks protocol version {ProtocolVersion}, not {requested}.",
                $"Use a client for protocol version {ProtocolVersion}, or a tx that speaks {requested}.",
                exitCode: 2);
            failure.ErrorData!["supported"] = new JsonArray(ProtocolVersion);
            throw failure;
        }

        var name = parameters["clientInfo"]?["name"] is JsonValue clientName && clientName.TryGetValue<string>(out var given) && given.Length > 0
            ? given
            : "client";
        var clientId = $"{name}-1";
        _methods.Attach(clientId, Notify);
        _initialized = true;
        Log($"initialized for {clientId}");

        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["serverInfo"] = new JsonObject { ["name"] = "tx", ["version"] = _serverVersion },
            ["clientId"] = clientId,
            ["capabilities"] = new JsonObject
            {
                ["methods"] = new JsonArray([.. _methods.Methods.Select(method => (JsonNode)method)]),
                ["notifications"] = new JsonArray([.. _methods.Notifications.Select(method => (JsonNode)method)])
            }
        };
    }

    private void HandleNotification(string method)
    {
        if (method == "exit")
        {
            _exitCode = _shutdown ? 0 : 1;
            Log($"exit ({_exitCode})");
            return;
        }

        // Unknown notifications, '$/' ones included, are ignored (docs/protocol.md, Method names).
        Log($"ignored notification {method}");
    }

    private void Cancel(JsonNode? parameters)
    {
        var id = parameters?["id"];
        if (id is not null && _pending.TryGetValue(Key(id), out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished while the cancellation was on its way.
            }
        }
    }

    /// <summary>Why <paramref name="message"/> is not a JSON-RPC 2.0 message, or <c>null</c>.</summary>
    private static string? Validate(JsonObject message)
    {
        if (message["jsonrpc"] is not JsonValue version || !version.TryGetValue<string>(out var text) || text != "2.0")
            return "'jsonrpc' must be \"2.0\".";
        if (message.ContainsKey("id") && ValidId(message) is null)
            return "'id' must be a string or a number.";
        if (message.ContainsKey("method"))
            return message["method"] is JsonValue method && method.TryGetValue<string>(out _) ? null : "'method' must be a string.";
        return message.ContainsKey("result") || message.ContainsKey("error") ? null : "The message has no 'method'.";
    }

    private static JsonNode? ValidId(JsonObject message)
        => message["id"] is JsonValue id && id.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? id : null;

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private void Notify(string method, JsonNode? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        Send(message);
    }

    private void SendError(JsonNode? id, int code, string message, JsonObject? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
            error["data"] = data.DeepClone();
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error });
    }

    private void Send(JsonObject message) => _writer.Send(message.ToJsonString(WriteOptions));

    private void Log(string line) => _log.WriteLine($"[tx serve] {line}");
}
