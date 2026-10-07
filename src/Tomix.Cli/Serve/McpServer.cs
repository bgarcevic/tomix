using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Tomix.Cli.Serve;

/// <summary>
/// The Model Context Protocol side of <c>tx mcp</c> (#354): <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c> and <c>tools/call</c> over JSON-RPC 2.0. A tool call is a request of the session
/// protocol made as this client, so every edit an agent makes is an undo step the other clients see.
/// Requests run one at a time in arrival order; a reader keeps reading meanwhile, so
/// <c>notifications/cancelled</c> reaches a call that is queued or running.
/// </summary>
internal sealed class McpServer
{
    /// <summary>The MCP versions this server speaks, newest first.</summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>What an agent should know before its first call; the harness passes it on.</summary>
    internal const string Instructions =
        """
        tomix (tx) edits a tabular semantic model (Power BI, Analysis Services, Fabric) in a live session. Each edit applies at once as an undo step, which a person watching in tx ui sees as it happens. Nothing reaches the model's files or server until session_save.

        - With no model open, call session_open with the model's TMDL folder or .bim file. A model the person has open in tx ui is joined.
        - Read with object_get, object_find, model_tree and deps_get; they see the session's unsaved edits. Object paths are slash-separated: 'Sales/Amount'.
        - Group related edits between transaction_begin (with a label) and transaction_commit, so the person can undo them as one step.
        - While the session is open, change the model only through these tools; never edit its .tmdl or .bim files directly.
        - Leave saving to the person unless they ask you to save.
        - When a call fails with TOMIX_SESSION_STALE, the model changed outside the session: tell the person and let them decide.
        """;

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IMessageChannel _channel;
    private readonly McpSession _session;
    private readonly IReadOnlyList<McpTool> _tools;
    private readonly string _serverVersion;
    private readonly TextWriter _log;
    private readonly Channel<JsonObject> _queue = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private bool _initialized;

    public McpServer(IMessageChannel channel, McpSession session, IReadOnlyList<McpTool> tools, string serverVersion, TextWriter log)
    {
        _channel = channel;
        _session = session;
        _tools = tools;
        _serverVersion = serverVersion;
        _log = log;
    }

    /// <summary>Serves until the end of input, which is how an MCP client ends a stdio session.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reading = Task.Run(() => ReadAsync(cancellationToken), CancellationToken.None);
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(CancellationToken.None))
                await HandleAsync(message);
        }
        finally
        {
            await _channel.CompleteAsync();
        }

        await reading;
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.ReadAsync(cancellationToken) is { } frame)
            {
                if (frame.Body is not { } body)
                {
                    SendError(null, ProtocolErrors.InvalidRequest, frame.Problem ?? "The message cannot be read.");
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(body);
                }
                catch (JsonException ex)
                {
                    SendError(null, ProtocolErrors.ParseError, $"The message is not valid JSON: {ex.Message}");
                    continue;
                }

                if (node is not JsonObject message || message["jsonrpc"] is not JsonValue version || (string?)version != "2.0")
                {
                    SendError(null, ProtocolErrors.InvalidRequest, "A message must be a JSON-RPC 2.0 object.");
                    continue;
                }

                if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
                {
                    // A response: the server sends no requests, so there is nothing to match.
                    continue;
                }

                if (!message.ContainsKey("id"))
                {
                    if (method == "notifications/cancelled")
                        Cancel(message["params"]?["requestId"]);
                    continue;
                }

                _pending[Key(message["id"])] = new CancellationTokenSource();
                await _queue.Writer.WriteAsync(message, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client closed its end: the same as the end of input.
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var method = (string)message["method"]!;
        var id = message["id"];
        var key = Key(id);
        var cancellation = _pending.GetValueOrDefault(key) ?? new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (message["params"] is not (null or JsonObject))
                throw ProtocolException.InvalidParams("params must be an object.");
            var parameters = message["params"] as JsonObject ?? [];
            var result = await DispatchAsync(method, parameters, cancellation.Token);
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });
            Log($"{Describe(method, parameters)} #{key} ok ({clock.ElapsedMilliseconds} ms)");
        }
        catch (ProtocolException ex)
        {
            SendError(id, ex.Code, ex.Message, ex.ErrorData);
            Log($"{method} #{key} error {ex.Code} ({clock.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A cancelled request is not answered (MCP, Cancellation).
            Log($"{method} #{key} cancelled");
        }
        catch (Exception ex)
        {
            SendError(id, ProtocolErrors.InternalError, $"Unexpected error: {ex.Message}");
            Log($"{method} #{key} failed: {ex}");
        }
        finally
        {
            _pending.TryRemove(key, out _);
            cancellation.Dispose();
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return await InitializeAsync(parameters, cancellationToken);
            case "ping":
                return new JsonObject();
        }

        if (!_initialized)
            throw new ProtocolException(ProtocolErrors.NotInitialized, "The server is not initialized; send 'initialize' first.");

        return method switch
        {
            "tools/list" => new JsonObject { ["tools"] = new JsonArray([.. _tools.Select(tool => (JsonNode)tool.Describe())]) },
            "tools/call" => await CallAsync(parameters, cancellationToken),
            _ => throw new ProtocolException(ProtocolErrors.MethodNotFound, $"Unknown method '{method}'.")
        };
    }

    private async Task<JsonObject> InitializeAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        if (_initialized)
            throw new ProtocolException(ProtocolErrors.InvalidRequest, "The server is already initialized.");

        // The client's version when this server speaks it, else the newest it speaks (MCP, Lifecycle).
        var requested = parameters["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var version = requested is not null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0];
        var name = parameters["clientInfo"]?["name"] is JsonValue clientName && clientName.TryGetValue<string>(out var given) && given.Length > 0
            ? given
            : "mcp";

        await _session.AttachAsync(name, cancellationToken);
        _initialized = true;
        Log($"initialized for {name} (MCP {version})");

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "tomix", ["title"] = "tomix", ["version"] = _serverVersion },
            ["instructions"] = Instructions
        };
    }

    private async Task<JsonObject> CallAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = parameters["name"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var tool = _tools.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw ProtocolException.InvalidParams(name is null ? "tools/call needs the tool's 'name'." : $"Unknown tool '{name}'.");
        var arguments = parameters["arguments"] switch
        {
            null => [],
            JsonObject given => given.DeepClone().AsObject(),
            _ => throw ProtocolException.InvalidParams("'arguments' must be an object.")
        };

        // Errors in a call go back to the agent as the tool's result, so it can correct itself.
        if (arguments.Select(pair => pair.Key).FirstOrDefault(key => !tool.Parameters.Contains(key)) is { } unknown)
            return Failure(new JsonObject
            {
                ["error"] = $"'{unknown}' is not an argument of {tool.Name}.",
                ["code"] = "TOMIX_MCP_INVALID_ARGUMENT",
                ["hint"] = $"It takes: {(tool.Parameters.Any() ? string.Join(", ", tool.Parameters) : "no arguments")}."
            });

        try
        {
            var result = await _session.CallAsync(tool.Method, arguments, cancellationToken);
            return new JsonObject
            {
                ["content"] = new JsonArray(Content(result.ToJsonString(WriteOptions))),
                ["isError"] = false
            };
        }
        catch (ProtocolException ex) when (ex.Code != ProtocolErrors.RequestCancelled)
        {
            return Failure(new JsonObject
            {
                ["error"] = ex.Message,
                ["code"] = (string?)ex.ErrorData?["code"] ?? (ex.Code == ProtocolErrors.InvalidParams ? "TOMIX_MCP_INVALID_ARGUMENT" : "TOMIX_UNEXPECTED"),
                ["hint"] = ex.ErrorData?["hint"]?.DeepClone()
            });
        }
    }

    private static JsonObject Failure(JsonObject error)
        => new()
        {
            ["content"] = new JsonArray(Content(error.ToJsonString(WriteOptions))),
            ["isError"] = true
        };

    private static JsonObject Content(string text) => new() { ["type"] = "text", ["text"] = text };

    private void Cancel(JsonNode? requestId)
    {
        if (requestId is not null && _pending.TryGetValue(Key(requestId), out var cancellation))
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

    private static string Describe(string method, JsonObject parameters)
        => method == "tools/call" && parameters["name"] is JsonValue name ? $"tools/call {name}" : method;

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private void SendError(JsonNode? id, int code, string message, JsonObject? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
            error["data"] = data.DeepClone();
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error });
    }

    private void Send(JsonObject message) => _channel.Send(message.ToJsonString(WriteOptions));

    private void Log(string line) => _log.WriteLine($"[tx mcp] {line}");
}
