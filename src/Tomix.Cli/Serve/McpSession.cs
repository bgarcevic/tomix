using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace Tomix.Cli.Serve;

/// <summary>
/// The session the tools of <c>tx mcp</c> work in, as one client of it: the session this process
/// holds, or, when a <c>tx ui</c> has the model open, that one, joined over its WebSocket so the
/// person sees every edit in the page (#369). <c>session.open</c> switches between the two: opening
/// a model a <c>tx ui</c> holds joins it, and opening any other model leaves it.
/// </summary>
internal sealed class McpSession : IAsyncDisposable
{
    private readonly SessionHost _host;
    private readonly LiveRegistry _registry;
    private readonly Func<LiveEntry, CancellationToken, Task<WebSocket>> _connect;
    private readonly LiveEntry? _joinAtStart;
    private string _name = "mcp";
    private ILink? _link;

    /// <param name="host">The session this process holds, with or without a model open; closed with this.</param>
    /// <param name="registry">Where running <c>tx ui</c> sessions are recorded.</param>
    /// <param name="connect">Connects to a <c>tx ui</c>'s session.</param>
    /// <param name="joinAtStart">The <c>tx ui</c> session to join instead of <paramref name="host"/>'s, if any.</param>
    public McpSession(
        SessionHost host,
        LiveRegistry registry,
        Func<LiveEntry, CancellationToken, Task<WebSocket>>? connect = null,
        LiveEntry? joinAtStart = null)
    {
        _host = host;
        _registry = registry;
        _connect = connect ?? ServeRelay.ConnectAsync;
        _joinAtStart = joinAtStart;
    }

    /// <summary>Joins the session as <paramref name="clientName"/>, the name the agent's harness gave.</summary>
    /// <exception cref="ProtocolException">The <c>tx ui</c> to join cannot be reached.</exception>
    public async Task AttachAsync(string clientName, CancellationToken cancellationToken)
    {
        _name = clientName;
        _link = _joinAtStart is { } entry ? await Remote.ConnectAsync(entry, clientName, _connect, cancellationToken) : new Local(_host, clientName);
    }

    /// <summary>Calls <paramref name="method"/> of the session protocol as this client.</summary>
    /// <exception cref="ProtocolException">The session answered with an error.</exception>
    public Task<JsonObject> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var link = _link ?? throw new InvalidOperationException("The session is not joined yet.");
        return method == "session.open" ? OpenAsync(parameters, cancellationToken) : link.RequestAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonObject> OpenAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = parameters["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var text) ? text : null;
        var server = parameters["server"] is JsonValue serverValue && serverValue.TryGetValue<string>(out var name) ? name : null;
        var database = parameters["database"] is JsonValue databaseValue && databaseValue.TryGetValue<string>(out var catalog) ? catalog : null;
        var reference = model is null && server is null ? null : _host.Opener.Resolve(model, server, database);
        var key = reference is { IsLocalPath: true } ? Path.GetFullPath(reference.Value) : reference?.Value;

        if (!string.IsNullOrWhiteSpace(key) && _registry.Find(key) is { } running)
        {
            if (_link is Remote { Entry: var current } && current == running)
                return await _link.RequestAsync("session.status", [], cancellationToken);
            if (_host.Session is { IsDirty: true } open)
                throw ProtocolException.Tomix(
                    "TOMIX_SESSION_DIRTY",
                    $"{open.Reference.Value} has unsaved changes in this session; joining tx ui on {running.Model} would discard them.",
                    "Save them with session_save, or undo them, first.",
                    exitCode: 1);

            var joined = await Remote.ConnectAsync(running, _name, _connect, cancellationToken);
            await _link!.DisposeAsync();
            await _host.ReplaceAsync(null);
            _link = joined;
            _host.Log.WriteLine($"[tx mcp] joined the session tx ui holds on {running.Model} (process {running.ProcessId})");
            return await _link.RequestAsync("session.status", [], cancellationToken);
        }

        if (_link is Remote)
        {
            await _link.DisposeAsync();
            _link = new Local(_host, _name);
        }

        var forwarded = parameters.DeepClone().AsObject();
        if (reference is { IsLocalPath: true })
            forwarded["model"] = key;
        return await _link!.RequestAsync("session.open", forwarded, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_link is { } link)
            await link.DisposeAsync();
        _link = null;
        await _host.CloseAsync();
    }

    private interface ILink : IAsyncDisposable
    {
        Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken);
    }

    /// <summary>A client of the session this process holds.</summary>
    private sealed class Local : ILink
    {
        private readonly ServeSession _client;

        public Local(SessionHost host, string name)
        {
            _client = host.Connect();
            _client.Attach(name, capabilities: null, notify: (_, _) => { });
        }

        public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
            => await _client.InvokeAsync(method, parameters, cancellationToken) as JsonObject ?? [];

        public ValueTask DisposeAsync() => _client.DetachAsync();
    }

    /// <summary>A client of the session a <c>tx ui</c> holds, over its WebSocket.</summary>
    private sealed class Remote : ILink
    {
        private readonly WebSocket _socket;
        private readonly WebSocketChannel _channel;
        private readonly ProtocolClient _client;

        private Remote(LiveEntry entry, WebSocket socket)
        {
            Entry = entry;
            _socket = socket;
            _channel = new WebSocketChannel(socket);
            _client = new ProtocolClient(_channel);
        }

        public LiveEntry Entry { get; }

        public static async Task<Remote> ConnectAsync(
            LiveEntry entry, string name, Func<LiveEntry, CancellationToken, Task<WebSocket>> connect, CancellationToken cancellationToken)
        {
            WebSocket socket;
            try
            {
                socket = await connect(entry, cancellationToken);
            }
            catch (WebSocketException ex)
            {
                throw Unreachable(entry, ex.Message);
            }

            var remote = new Remote(entry, socket);
            try
            {
                await remote.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = ProtocolServer.ProtocolVersion,
                    ["clientInfo"] = new JsonObject { ["name"] = name }
                }, cancellationToken);
                return remote;
            }
            catch
            {
                await remote.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.RequestAsync(method, parameters, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or WebSocketException)
            {
                throw Unreachable(Entry, ex.Message);
            }
        }

        /// <summary>Leaves the session, which stays with <c>tx ui</c> and keeps what is unsaved.</summary>
        public async ValueTask DisposeAsync()
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _client.RequestAsync("shutdown", null, patience.Token);
                _client.Notify("exit");
                // The session closes the socket once it has let this client go.
                while (await _channel.ReadAsync(patience.Token) is not null)
                {
                }
            }
            catch (Exception ex) when (ex is ProtocolException or InvalidOperationException or WebSocketException or OperationCanceledException)
            {
                // tx ui has gone already, or does not answer: the client is gone either way.
            }
            finally
            {
                await _channel.CompleteAsync();
                _socket.Dispose();
            }
        }

        private static ProtocolException Unreachable(LiveEntry entry, string reason)
            => ProtocolException.Tomix(
                "TOMIX_UI_UNREACHABLE",
                $"{entry.Model} is open in tx ui (process {entry.ProcessId}), but its session cannot be reached: {reason}",
                "Ask the person whether tx ui is still running; call session_open again once it is.",
                exitCode: 2);
    }
}
