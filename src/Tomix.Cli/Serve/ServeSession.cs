using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.App.Models;
using Tomix.App.Session;
using Tomix.Cli.Commands;
using Tomix.Cli.Interactive;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Serve;

/// <summary>
/// One client's side of the shared session (docs/protocol.md): its name, its transaction and its
/// requests. Most methods run a command of the same command tree <c>tx interactive</c> uses, with
/// JSON output captured as the result; opening, closing, the snapshot and the tree act on the
/// session directly. Notifications reach every client through the <see cref="SessionHost"/>.
/// </summary>
internal sealed class ServeSession : IProtocolMethods
{
    private readonly SessionHost _host;
    private Action<string, JsonNode?> _notify = (_, _) => { };
    private string _client = "client";
    private bool _joined;
    private ILiveModelSession? _bound;
    private SessionScope? _scope;
    private LiveSessionHandler? _handler;
    private RootCommand? _root;

    internal ServeSession(SessionHost host)
    {
        _host = host;
    }

    public IReadOnlyList<string> Methods { get; } =
    [
        "session.open", "session.close", "session.status", "session.save", "session.snapshot", "session.history",
        "session.undo", "session.redo", "transaction.begin", "transaction.commit", "transaction.rollback",
        "model.summary", "model.tree", "object.get", "object.find", "deps.get", "object.add", "object.set",
        "object.remove", "object.move", "model.replace", "bpa.run", "bpa.fix", "dax.format", "dax.check", "command.run"
    ];

    public IReadOnlyList<string> Notifications { get; } =
        ["model.changed", "session.state", "session.saved", "transaction.opened", "transaction.closed"];

    public string Attach(string clientName, Action<string, JsonNode?> notify)
    {
        _notify = notify;
        _client = _host.Reserve(clientName);
        _joined = true;
        _host.Join(this);
        return _client;
    }

    /// <summary>The client's ID once it has sent <c>initialize</c>.</summary>
    internal string? Id => _joined ? _client : null;

    internal void Notify(string method, JsonNode? parameters) => _notify(method, parameters);

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "session.open":
                return await OpenAsync(parameters, cancellationToken);
            case "session.close":
                return await CloseAsync(parameters, cancellationToken);
        }

        var scope = Bind() ?? throw ProtocolException.Tomix(
            "TOMIX_SESSION_NO_MODEL", $"No model is open for '{method}'.", "Send 'session.open' first.", exitCode: 2);
        await ResolveIdAsync(scope, method, parameters, cancellationToken);
        if (method == "session.snapshot")
            return Envelope(await SnapshotAsync(scope, cancellationToken), scope);
        if (method == "model.tree")
            return Envelope(await TreeAsync(scope, parameters, cancellationToken), scope);
        if (method == "command.run")
            return Envelope(await RunCommandAsync(parameters, cancellationToken), scope);

        if (method == "deps.get")
            Direction(parameters);
        var route = ProtocolRoutes.All[method];
        var result = await RunAsync(method, ProtocolRoutes.Arguments(method, route, parameters, _root!), cancellationToken);
        if (method == "object.get")
            await AddIdsAsync(scope, result["data"], cancellationToken);
        Announce(method, parameters, result);
        return result;
    }

    /// <summary>
    /// The client has gone: its open transaction is rolled back, and the session stays with the
    /// host for the other clients. The host decides when the session itself closes.
    /// </summary>
    public async ValueTask DetachAsync()
    {
        if (Bind() is not null && _handler!.InTransaction)
        {
            var transaction = _handler.Status().Data!.Transaction!.Id;
            _host.Log.WriteLine($"[tx serve] {_client} left with a transaction open; it is rolled back");
            await _handler.RollbackOpenTransactionAsync(CancellationToken.None);
            _host.Broadcast("transaction.closed", new JsonObject
            {
                ["transaction"] = transaction,
                ["client"] = _client,
                ["outcome"] = "rolledBack"
            });
        }

        if (_joined)
            _host.Leave(this);
    }

    /// <summary>
    /// The host's session as this client sees it: a command tree and session commands that act
    /// under this client's name, rebuilt when the host opens another model.
    /// </summary>
    private SessionScope? Bind()
    {
        var session = _host.Session;
        if (ReferenceEquals(session, _bound) && _root is not null)
            return _scope;

        _bound = session;
        _scope = session is null ? null : new SessionScope(new LiveSessionSource(session, new LiveLeaseOptions(_client)));
        _handler = session is null ? null : new LiveSessionHandler(session, _client);
        _root = _host.BuildRoot(_scope, _handler is null ? [] : SessionCommands.Build(_handler));
        return _scope;
    }

    private async Task<JsonNode> OpenAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = Text(parameters, "model");
        var server = Text(parameters, "server");
        var database = Text(parameters, "database");
        var discard = Flag(parameters, "discard");
        Only(parameters, "session.open", "model", "server", "database", "discard");
        if (model is null && server is null)
            throw ProtocolException.InvalidParams("session.open needs 'model', or 'server' and 'database'.");

        Bind();
        if (_host.Session is not null && _host.HasOthers(this))
            throw InUse("open another model");
        if (_handler is { HasUnsavedWork: true } && !discard)
            throw Dirty("open another model");

        var reference = _host.Opener.Resolve(model, server, database);
        var (session, failure) = await OpenOrFailAsync(reference, cancellationToken);
        if (failure is not null)
            throw ProtocolException.Tomix(failure.Code, failure.Message, failure.Hint, exitCode: 2);

        await _host.ReplaceAsync(session);
        var scope = Bind()!;
        return Envelope(StatusData(), scope);
    }

    private async Task<(ILiveModelSession?, TomixDiagnostic?)> OpenOrFailAsync(ModelReference reference, CancellationToken cancellationToken)
    {
        try
        {
            return await _host.Opener.TryOpenAsync(reference, showSpinner: false, cancellationToken);
        }
        catch (ModelLoadException ex)
        {
            return (null, new TomixDiagnostic(
                "TOMIX_MODEL_LOAD_FAILED",
                DiagnosticSeverity.Error,
                ex.Message,
                "Fix the model source and retry; the message lists what could not be loaded."));
        }
    }

    private async Task<JsonNode> CloseAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var save = Flag(parameters, "save");
        var discard = Flag(parameters, "discard");
        Only(parameters, "session.close", "save", "discard");
        var scope = Bind() ?? throw ProtocolException.Tomix(
            "TOMIX_SESSION_NO_MODEL", "No model is open to close.", "Send 'session.open' first.", exitCode: 2);
        if (save && discard)
            throw ProtocolException.InvalidParams("session.close takes 'save' or 'discard', not both.");
        if (_host.HasOthers(this))
            throw InUse("close the session");

        if (save)
        {
            if (_handler!.InTransaction)
                throw ProtocolException.Tomix(
                    "TOMIX_SESSION_TRANSACTION_OPEN",
                    "A transaction is open; closing would save part of it.",
                    "Send 'transaction.commit' or 'transaction.rollback' first.",
                    exitCode: 2);
            var saved = await RunAsync("session.save", ["save"], cancellationToken);
            Announce("session.save", [], saved);
        }
        else if (_handler!.HasUnsavedWork && !discard)
        {
            throw Dirty("close the session");
        }

        var version = scope.Session.Version;
        await _handler!.RollbackOpenTransactionAsync(cancellationToken);
        await _host.ReplaceAsync(null);
        return new JsonObject
        {
            ["data"] = new JsonObject { ["closed"] = true, ["saved"] = save },
            ["diagnostics"] = new JsonArray(),
            ["version"] = version
        };
    }

    private ProtocolException Dirty(string action)
        => ProtocolException.Tomix(
            "TOMIX_SESSION_DIRTY",
            $"The session has unsaved changes{(_handler!.InTransaction ? " or an open transaction" : "")}; '{action}' would discard them.",
            "Send 'session.save' first, or pass \"discard\": true.",
            exitCode: 1);

    private ProtocolException InUse(string action)
        => ProtocolException.Tomix(
            "TOMIX_SESSION_IN_USE",
            $"Other clients are working in this session; '{action}' would take it from them.",
            "Disconnect the other clients first, or start another 'tx ui' or 'tx serve' for the other model.",
            exitCode: 1);

    /// <summary>
    /// Runs one command on the session with JSON output and returns its envelope plus the session
    /// version. A command that prints a result is answered with it even when its exit code is not 0
    /// (<c>dax.check</c> on an invalid model); one that prints only an error fails with that error.
    /// </summary>
    private async Task<JsonObject> RunAsync(string method, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var scope = _scope!;
        scope.Source.Options = new LiveLeaseOptions(_client, method);
        string[] args = [.. arguments.TakeWhile(arg => arg != "--"), "--output-format", "json", "--error-format", "json", "--quiet", "--non-interactive", .. arguments.SkipWhile(arg => arg != "--")];
        var parseResult = _root!.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parseResult.Errors.Count > 0)
            throw ProtocolException.InvalidParams(string.Join(" ", parseResult.Errors.Select(error => error.Message)));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await ConsoleRouting.CaptureAsync(stdout, stderr, async () =>
        {
            try
            {
                return await parseResult.InvokeAsync(
                    new InvocationConfiguration { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Program.ReportFailure(ex, parseResult);
            }
        });
        cancellationToken.ThrowIfCancellationRequested();

        if (stderr.ToString() is { Length: > 0 } commentary && Json(commentary) is not JsonObject)
            _host.Log.Write(commentary);
        if (Json(stdout.ToString()) is JsonObject { } envelope && envelope.ContainsKey("data"))
        {
            envelope["version"] = scope.Session.Version;
            return envelope;
        }

        if (Json(stderr.ToString()) is JsonObject error)
            throw ProtocolException.Tomix(
                (string?)error["code"] ?? "TOMIX_UNEXPECTED",
                (string?)error["error"] ?? $"{method} failed.",
                (string?)error["hint"],
                exitCode == 0 ? 1 : exitCode);

        throw new ProtocolException(
            ProtocolErrors.InternalError,
            $"{method} produced no result (exit code {exitCode}).",
            new JsonObject { ["code"] = "TOMIX_UNEXPECTED", ["output"] = stdout + stderr.ToString() });
    }

    /// <summary>
    /// <c>command.run</c>: a one-shot command a caller routed to this session (#400), run as it would
    /// run in <c>tx interactive</c>, with its output and exit code as the result. Only the commands
    /// <see cref="LiveCommandRoute.Commands"/> lists run, so a caller cannot reopen the session.
    /// </summary>
    private async Task<JsonObject> RunCommandAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        Only(parameters, "command.run", "args", "stdin", "colorSystem", "width");
        if (parameters["args"] is not JsonArray items || items.Count == 0)
            throw ProtocolException.InvalidParams("command.run needs 'args': the command line, for example [\"set\", \"Sales/Margin\", \"--set\", \"FormatString=0%\"].");
        var args = items.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw ProtocolException.InvalidParams("'args' must be an array of strings.")).ToArray();
        var stdin = Text(parameters, "stdin");
        var colors = Text(parameters, "colorSystem") is { } name
            ? Enum.TryParse<Spectre.Console.ColorSystem>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw ProtocolException.InvalidParams($"'colorSystem' must be NoColors, Legacy, Standard, EightBit or TrueColor, not '{name}'.")
            : Spectre.Console.ColorSystem.NoColors;
        int? width = parameters["width"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var columns) && columns > 0 => columns,
            _ => throw ProtocolException.InvalidParams("'width' must be a positive whole number.")
        };

        // Nothing in the session can ask the person anything: a command that would prompt fails instead.
        var parseResult = _root!.Parse(["--non-interactive", .. args], new ParserConfiguration { ResponseFileTokenReplacer = null });
        var command = LiveCommandRoute.CommandPath(parseResult.CommandResult);
        if (!LiveCommandRoute.Commands.Contains(command))
            throw ProtocolException.InvalidParams(
                $"'{(command.Length == 0 ? string.Join(' ', args) : command)}' cannot run through command.run; it runs {string.Join(", ", LiveCommandRoute.Commands)}.");
        if (parseResult.Errors.Count > 0)
            throw ProtocolException.InvalidParams(string.Join(" ", parseResult.Errors.Select(error => error.Message)));

        var session = _scope!.Session;
        var wasDirty = session.IsDirty;
        _scope.Source.Options = new LiveLeaseOptions(_client, command);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await ConsoleRouting.CaptureAsync(stdout, stderr, async () =>
        {
            InputValueResolver.Stdin.Value = stdin is null ? null : new StringReader(stdin);
            try
            {
                return await parseResult.InvokeAsync(
                    new InvocationConfiguration { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Program.ReportFailure(ex, parseResult);
            }
        }, new CapturedTerminal(colors, width));
        cancellationToken.ThrowIfCancellationRequested();

        // 'save', or an edit with --save, wrote the session back to its source.
        if (wasDirty && !session.IsDirty)
            _host.Broadcast("session.saved", new JsonObject
            {
                ["version"] = session.Version,
                ["savedTo"] = session.Reference.Value,
                ["persistence"] = "file"
            });

        return new JsonObject
        {
            ["exitCode"] = exitCode,
            ["stdout"] = stdout.ToString(),
            ["stderr"] = stderr.ToString()
        };
    }

    private static JsonNode? Json(string text)
    {
        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The events a successful request implies beyond <c>model.changed</c> and <c>session.state</c>.</summary>
    private void Announce(string method, JsonObject parameters, JsonObject result)
    {
        var data = result["data"];
        switch (method)
        {
            case "session.save" when !parameters.ContainsKey("outputFile"):
                _host.Broadcast("session.saved", new JsonObject
                {
                    ["version"] = result["version"]?.DeepClone(),
                    ["savedTo"] = data?["savedTo"]?.DeepClone(),
                    ["persistence"] = data?["persistence"]?.DeepClone()
                });
                break;
            case "transaction.begin":
                _host.Broadcast("transaction.opened", new JsonObject
                {
                    ["transaction"] = data?["transaction"]?.DeepClone(),
                    ["client"] = _client,
                    ["label"] = data?["label"]?.DeepClone()
                });
                break;
            case "transaction.commit" or "transaction.rollback":
                _host.Broadcast("transaction.closed", new JsonObject
                {
                    ["transaction"] = data?["transaction"]?.DeepClone(),
                    ["client"] = _client,
                    ["outcome"] = method == "transaction.commit" ? "committed" : "rolledBack"
                });
                break;
        }
    }

    /// <summary>Replaces an <c>id</c> parameter with the path it names (docs/protocol.md, Addressing objects).</summary>
    private static async Task ResolveIdAsync(SessionScope scope, string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        if (!parameters.ContainsKey("id"))
            return;
        if (parameters.ContainsKey("path"))
            throw ProtocolException.InvalidParams($"{method} takes 'path' or 'id', not both.");

        var text = Text(parameters, "id");
        if (!ObjectId.TryParse(text, out var id))
            throw ProtocolException.InvalidParams($"'{text}' is not an object ID; IDs look like \"o1k3\".");

        var snapshot = await scope.Session.GetLiveSnapshotAsync(cancellationToken);
        if (!snapshot.Index.TryGetPath(id, out var path))
            throw ProtocolException.Tomix(
                "TOMIX_OBJECT_NOT_FOUND",
                $"No object has ID {text} in this session.",
                "IDs last only for the session; call 'session.snapshot' for the current ones.",
                exitCode: 1);

        parameters.Remove("id");
        parameters["path"] = path;
    }

    /// <summary><c>direction</c> of <c>deps.get</c> as the flags of <c>tx deps</c>.</summary>
    private static void Direction(JsonObject parameters)
    {
        if (!parameters.ContainsKey("direction"))
            return;

        var direction = Text(parameters, "direction");
        parameters.Remove("direction");
        switch (direction)
        {
            case "upstream":
                parameters["upstream"] = true;
                break;
            case "downstream":
                parameters["downstream"] = true;
                break;
            case "both":
                break;
            default:
                throw ProtocolException.InvalidParams($"'direction' must be upstream, downstream or both, not '{direction}'.");
        }
    }

    private async Task<JsonObject> SnapshotAsync(SessionScope scope, CancellationToken cancellationToken)
    {
        var snapshot = await scope.Session.GetLiveSnapshotAsync(cancellationToken);
        var objects = new JsonArray();
        var pending = new Stack<ModelObject>(snapshot.Snapshot.Objects.Reverse());
        while (pending.TryPop(out var node))
        {
            objects.Add(Describe(node));
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }

        return new JsonObject { ["name"] = snapshot.Snapshot.Name, ["objects"] = objects };
    }

    private async Task<JsonObject> TreeAsync(SessionScope scope, JsonObject parameters, CancellationToken cancellationToken)
    {
        var path = Text(parameters, "path");
        Only(parameters, "model.tree", "path");
        var snapshot = await scope.Session.GetLiveSnapshotAsync(cancellationToken);
        IReadOnlyList<ModelObject> children = snapshot.Snapshot.Objects;
        JsonNode? parent = null;
        if (path is not null)
        {
            if (!snapshot.Index.TryGetId(path.Trim('/'), out var id) || !snapshot.Index.TryGetObject(id, out var node))
                throw ProtocolException.Tomix(
                    "TOMIX_OBJECT_NOT_FOUND",
                    $"Object not found: {path}",
                    "Call 'model.tree' without a path for the top level, then walk down.",
                    exitCode: 1);
            children = node.Children;
            parent = new JsonObject { ["id"] = node.Id?.ToString(), ["path"] = node.Path };
        }

        var items = new JsonArray();
        foreach (var child in children)
        {
            var item = Describe(child);
            item["hasChildren"] = child.Children.Count > 0;
            items.Add(item);
        }

        return new JsonObject { ["parent"] = parent, ["children"] = items };
    }

    private static JsonObject Describe(ModelObject node)
        => new()
        {
            ["id"] = node.Id?.ToString(),
            ["type"] = node.Kind.ToString(),
            ["path"] = node.Path,
            ["name"] = node.Name
        };

    /// <summary>Adds the session ID to each object in an <c>object.get</c> result, first among its fields.</summary>
    private static async Task AddIdsAsync(SessionScope scope, JsonNode? data, CancellationToken cancellationToken)
    {
        var index = (await scope.Session.GetLiveSnapshotAsync(cancellationToken)).Index;
        var pending = new Stack<JsonNode?>([data]);
        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case JsonObject item:
                    if (!item.ContainsKey("id") && item["path"] is JsonValue path && path.TryGetValue<string>(out var text)
                        && index.TryGetId(text, out var id))
                        item.Insert(0, "id", id.ToString());
                    foreach (var (_, child) in item)
                        pending.Push(child);
                    break;
                case JsonArray items:
                    foreach (var child in items)
                        pending.Push(child);
                    break;
            }
        }
    }

    private JsonObject StatusData()
    {
        var status = _handler!.Status().Data!;
        return new JsonObject
        {
            ["model"] = status.Model,
            ["source"] = status.Source,
            ["state"] = JsonSerializer.SerializeToNode(status.State, ProtocolJsonContext.Default.SessionState),
            ["dirty"] = status.Dirty,
            ["version"] = status.Version,
            ["undoSteps"] = status.UndoSteps,
            ["redoSteps"] = status.RedoSteps,
            ["transaction"] = null
        };
    }

    private static JsonObject Envelope(JsonObject data, SessionScope scope)
        => new() { ["data"] = data, ["diagnostics"] = new JsonArray(), ["version"] = scope.Session.Version };

    private static string? Text(JsonObject parameters, string name)
        => parameters[name] switch
        {
            null => null,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            _ => throw ProtocolException.InvalidParams($"'{name}' must be a string.")
        };

    private static bool Flag(JsonObject parameters, string name)
        => parameters[name] switch
        {
            null => false,
            JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            _ => throw ProtocolException.InvalidParams($"'{name}' must be true or false.")
        };

    private static void Only(JsonObject parameters, string method, params string[] names)
    {
        if (parameters.Select(pair => pair.Key).FirstOrDefault(key => !names.Contains(key) && key != "progressToken") is { } unknown)
            throw ProtocolException.InvalidParams($"'{unknown}' is not a parameter of {method}.");
    }
}
