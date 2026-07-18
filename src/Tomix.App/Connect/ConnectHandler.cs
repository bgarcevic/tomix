using Tomix.App.State;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Connect;

public sealed class ConnectHandler
{
    private readonly CliStateStore _store;

    public ConnectHandler(CliStateStore store) => _store = store;

    public TomixResult<ConnectShowResult> Show()
    {
        var state = _store.LoadCurrentSession();
        return TomixResult<ConnectShowResult>.Ok(new ConnectShowResult(state is not null, state));
    }

    public TomixResult<ConnectClearResult> Clear()
    {
        var existed = _store.LoadCurrentSession() is not null;
        _store.ClearCurrentSession();
        return TomixResult<ConnectClearResult>.Ok(new ConnectClearResult(existed));
    }

    public TomixResult<ConnectSetResult> Set(ConnectSetRequest request)
    {
        CliConnectionState? state;
        if (!string.IsNullOrWhiteSpace(request.Profile))
        {
            var profiles = _store.LoadProfiles();
            if (!profiles.TryGetValue(request.Profile, out var profile))
                return TomixResult<ConnectSetResult>.Fail(
                    "TOMIX_PROFILE_NOT_FOUND",
                    $"Profile '{request.Profile}' not found",
                    exitCode: 1);

            state = new CliConnectionState(
                profile.Server,
                profile.Database,
                NormalizeLocalPath(profile.Model),
                profile.Auth,
                Local: !string.IsNullOrWhiteSpace(profile.Model),
                Profile: profile.Name);
        }
        else if (!string.IsNullOrWhiteSpace(request.Model))
        {
            state = new CliConnectionState(
                null,
                request.Database,
                NormalizeLocalPath(request.Model),
                request.Auth,
                Local: true,
                Profile: null,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth);
        }
        else if (request.Local)
        {
            state = new CliConnectionState(
                null,
                request.Database,
                null,
                request.Auth,
                Local: true,
                Profile: null,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth);
        }
        else
        {
            state = new CliConnectionState(
                request.Server,
                request.Database,
                null,
                request.Auth,
                Local: false,
                Profile: null,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth);
        }

        _store.SaveCurrentSession(state);
        _store.AddRecentConnection(state);
        return TomixResult<ConnectSetResult>.Ok(new ConnectSetResult(Active: true, state));
    }

    public TomixResult<ConnectRecentListResult> Recents()
        => TomixResult<ConnectRecentListResult>.Ok(
            new ConnectRecentListResult(_store.LoadRecentConnections()));

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ModelReference.IsRemoteEndpoint(path) || Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path);
    }
}

public sealed record ConnectSetRequest(
    string? Server,
    string? Database,
    string? Model,
    string? Auth,
    bool Local,
    string? Profile,
    string? Workspace = null,
    string? WorkspaceFormat = null,
    string? WorkspaceAuth = null);
