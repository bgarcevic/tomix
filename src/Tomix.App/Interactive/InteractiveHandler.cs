using Tomix.App.State;
using Tomix.Core.Results;

namespace Tomix.App.Interactive;

public sealed class InteractiveHandler
{
    private readonly CliStateStore _store;

    public InteractiveHandler(CliStateStore store) => _store = store;

    public TomixResult<InteractiveStartResult> Start(InteractiveStartRequest request)
    {
        var model = NullIfBlank(request.Model);
        var server = NullIfBlank(request.Server);
        var database = NullIfBlank(request.Database);

        CliConnectionState? state = null;
        if (model is not null)
        {
            state = new CliConnectionState(
                Server: null,
                Database: database,
                Model: model,
                Auth: NullIfBlank(request.Auth),
                Local: true,
                Profile: null);
            _store.SaveCurrentSession(state);
        }
        else if (request.Local || server is not null || database is not null)
        {
            state = new CliConnectionState(
                Server: server,
                Database: database,
                Model: null,
                Auth: NullIfBlank(request.Auth),
                Local: request.Local,
                Profile: null);
            _store.SaveCurrentSession(state);
        }
        else
        {
            state = _store.LoadCurrentSession();
        }

        return TomixResult<InteractiveStartResult>.Ok(new InteractiveStartResult(
            _store.CurrentSessionId,
            _store.CurrentSessionFile,
            state is not null,
            state));
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record InteractiveStartRequest(
    string? Model,
    string? Server,
    string? Database,
    string? Auth,
    bool Local);

public sealed record InteractiveStartResult(
    string SessionId,
    string Path,
    bool Active,
    CliConnectionState? Connection);
