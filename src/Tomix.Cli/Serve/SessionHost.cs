using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.App.Models;
using Tomix.Cli.Interactive;
using Tomix.Core.Models;

namespace Tomix.Cli.Serve;

/// <summary>
/// One live session shared by every client connected to this process (ADR 0001 §1): the stdio
/// client of <c>tx serve</c>, or the browser and agents connected to <c>tx ui</c>. Each client is a
/// <see cref="ServeSession"/> with its own name, transaction and requests; the host owns the
/// session, hands out client IDs and sends every change notification to every client.
/// </summary>
internal sealed class SessionHost
{
    private readonly Lock _sync = new();
    private readonly List<ServeSession> _clients = [];
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private ILiveModelSession? _session;
    private JsonObject? _transaction;
    private JsonObject? _lastChange;
    private string _status = "{}";

    public SessionHost(
        Func<SessionScope?, IEnumerable<Command>, RootCommand> buildRoot,
        SessionOpener opener,
        TextWriter log,
        ILiveModelSession? session,
        TimeProvider? time = null)
    {
        BuildRoot = buildRoot;
        Opener = opener;
        Log = log;
        _time = time ?? TimeProvider.System;
        Use(session);
        RefreshStatus();
    }

    public Func<SessionScope?, IEnumerable<Command>, RootCommand> BuildRoot { get; }

    public SessionOpener Opener { get; }

    public TextWriter Log { get; }

    /// <summary>The open session, or <c>null</c> before <c>session.open</c> and after <c>session.close</c>.</summary>
    public ILiveModelSession? Session => _session;

    /// <summary>Raised after a client connects or leaves; <c>tx ui</c> ends the process on it.</summary>
    public event EventHandler? ClientsChanged;

    /// <summary>
    /// The body of <c>GET /status</c> (docs/protocol.md, Status endpoint), rebuilt whenever the
    /// session, its clients or its transaction change, so reading it never touches the model.
    /// </summary>
    public string Status => Volatile.Read(ref _status);

    public int ClientCount
    {
        get
        {
            lock (_sync)
                return _clients.Count;
        }
    }

    /// <summary>A new connection's methods; it joins the host when the client sends <c>initialize</c>.</summary>
    public ServeSession Connect() => new(this);

    /// <summary>A new client ID: the name the client gave, numbered per name (<c>mcp-1</c>, <c>mcp-2</c>).</summary>
    internal string Reserve(string name)
    {
        lock (_sync)
        {
            var number = _names.GetValueOrDefault(name) + 1;
            _names[name] = number;
            return $"{name}-{number}";
        }
    }

    internal void Join(ServeSession client)
    {
        lock (_sync)
            _clients.Add(client);

        RefreshStatus();
        ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Leave(ServeSession client)
    {
        bool removed;
        lock (_sync)
            removed = _clients.Remove(client);
        if (!removed)
            return;
        RefreshStatus();
        ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a client other than <paramref name="client"/> is connected.</summary>
    internal bool HasOthers(ServeSession client)
    {
        lock (_sync)
            return _clients.Any(other => !ReferenceEquals(other, client));
    }

    /// <summary>Sends a notification to every connected client.</summary>
    public void Broadcast(string method, JsonNode? parameters)
    {
        ServeSession[] clients;
        lock (_sync)
            clients = [.. _clients];
        Track(method, parameters);
        foreach (var client in clients)
            client.Notify(method, parameters?.DeepClone());
    }

    /// <summary>Makes <paramref name="session"/> the open one, closing the one before it.</summary>
    internal async Task ReplaceAsync(ILiveModelSession? session)
    {
        if (_session is { } previous && !ReferenceEquals(previous, session))
        {
            // Disposed first, so clients hear that it closed.
            await previous.DisposeAsync();
            previous.Changed -= OnChanged;
            previous.StateChanged -= OnStateChanged;
        }

        Use(session);
    }

    /// <summary>Closes the session, discarding what is unsaved, when the process ends.</summary>
    public async ValueTask CloseAsync()
    {
        if (_session is { IsDirty: true } session)
            Log.WriteLine($"[tx serve] unsaved changes to {session.Reference.Value} are discarded");
        await ReplaceAsync(null);
    }

    private void Use(ILiveModelSession? session)
    {
        if (session is not null && !ReferenceEquals(_session, session))
        {
            session.Changed += OnChanged;
            session.StateChanged += OnStateChanged;
        }

        _session = session;
        lock (_sync)
        {
            _transaction = null;
            _lastChange = null;
        }

        RefreshStatus();
    }

    /// <summary>Keeps what <c>/status</c> reports beyond the session's own counters.</summary>
    private void Track(string method, JsonNode? parameters)
    {
        lock (_sync)
        {
            switch (method)
            {
                case "model.changed":
                    _lastChange = new JsonObject
                    {
                        ["version"] = parameters?["version"]?.DeepClone(),
                        ["client"] = parameters?["origin"]?["client"]?.DeepClone(),
                        ["at"] = _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
                    };
                    break;
                case "transaction.opened":
                    _transaction = new JsonObject
                    {
                        ["id"] = parameters?["transaction"]?.DeepClone(),
                        ["client"] = parameters?["client"]?.DeepClone(),
                        ["label"] = parameters?["label"]?.DeepClone()
                    };
                    break;
                case "transaction.closed":
                    _transaction = null;
                    break;
            }
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var session = _session;
        JsonObject status;
        lock (_sync)
        {
            status = new JsonObject
            {
                ["protocolVersion"] = ProtocolServer.ProtocolVersion,
                ["model"] = session?.Reference.Value,
                ["state"] = session is null ? "closed" : JsonSerializer.SerializeToNode(session.State, ProtocolJsonContext.Default.SessionState),
                ["dirty"] = session?.IsDirty ?? false,
                ["version"] = session?.Version ?? 0,
                ["undoSteps"] = session?.History.Count(step => !step.Undone) ?? 0,
                ["redoSteps"] = session?.History.Count(step => step.Undone) ?? 0,
                ["transaction"] = _transaction?.DeepClone(),
                ["clients"] = new JsonArray([.. _clients.Where(client => client.Id is not null).Select(client => (JsonNode)client.Id!)]),
                ["lastChange"] = _lastChange?.DeepClone()
            };
        }

        Volatile.Write(ref _status, status.ToJsonString());
    }

    private void OnChanged(object? sender, ModelChangeBatch batch)
        => Broadcast("model.changed", JsonSerializer.SerializeToNode(batch, ProtocolJsonContext.Default.ModelChangeBatch));

    private void OnStateChanged(object? sender, SessionStateChange change)
        => Broadcast("session.state", new JsonObject
        {
            ["previous"] = JsonSerializer.SerializeToNode(change.Previous, ProtocolJsonContext.Default.SessionState),
            ["state"] = JsonSerializer.SerializeToNode(change.Current, ProtocolJsonContext.Default.SessionState),
            ["version"] = change.Version
        });
}
