using System.Text.Json.Nodes;

namespace Tomix.Cli.Serve;

/// <summary>
/// A client of the session protocol over <paramref name="channel"/>, for a process that joins the
/// session a <c>tx ui</c> holds: requests go one at a time, and notifications, which these callers
/// do not wait for, are skipped.
/// </summary>
internal sealed class ProtocolClient(IMessageChannel channel)
{
    private static readonly TimeSpan CancelPatience = TimeSpan.FromSeconds(5);

    private int _nextId;

    /// <summary>Sends <paramref name="method"/> and waits for its answer.</summary>
    /// <exception cref="ProtocolException">The session answered with an error.</exception>
    /// <exception cref="InvalidOperationException">The session closed before it answered.</exception>
    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            request["params"] = parameters;
        channel.Send(request.ToJsonString());

        // Cancelling asks the session to stop the request, which it answers with an error. Waiting
        // for that answer keeps the connection usable; a session that never answers is left.
        using var patience = new CancellationTokenSource();
        await using var cancel = cancellationToken.Register(() =>
        {
            channel.Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "$/cancelRequest",
                ["params"] = new JsonObject { ["id"] = id }
            }.ToJsonString());
            patience.CancelAfter(CancelPatience);
        });

        while (await channel.ReadAsync(patience.Token) is { } frame)
        {
            if (frame.Body is not { } body || JsonNode.Parse(body) is not JsonObject message || (int?)message["id"] != id)
                continue;
            if (message["error"] is JsonObject error)
                throw new ProtocolException((int?)error["code"] ?? ProtocolErrors.InternalError, (string?)error["message"] ?? $"{method} failed.", error["data"] as JsonObject);
            return message["result"] as JsonObject ?? [];
        }

        throw new InvalidOperationException($"the session closed before it answered {method}.");
    }

    /// <summary>Notifies the session, which sends no answer.</summary>
    public void Notify(string method) => channel.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());
}
