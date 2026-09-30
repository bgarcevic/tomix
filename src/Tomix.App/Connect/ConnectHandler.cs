using Tomix.App.State;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Connect;

public sealed class ConnectHandler
{
    private readonly CliStateStore _store;
    private readonly Func<string?, string?, bool> _stillServes;
    private readonly Func<string?, bool> _isListening;
    private readonly Func<string, PowerBiDesktopInstance?> _findInstance;

    /// <param name="stillServes">
    /// Validates a cached Desktop report name against the live instance; defaults to
    /// <see cref="PowerBiDesktopDiscovery.StillServes(string?, string?)"/>. Injectable so tests do
    /// not depend on a real listener.
    /// </param>
    /// <param name="isListening">
    /// Whether a <c>localhost:&lt;port&gt;</c> endpoint still has a listener; defaults to
    /// <see cref="PowerBiDesktopDiscovery.IsListening(string?)"/>.
    /// </param>
    /// <param name="findInstance">
    /// Looks up the live Desktop instance on an endpoint when no report name is cached; defaults to
    /// <see cref="PowerBiDesktopDiscovery.FindInstance(string)"/>.
    /// </param>
    public ConnectHandler(
        CliStateStore store,
        Func<string?, string?, bool>? stillServes = null,
        Func<string?, bool>? isListening = null,
        Func<string, PowerBiDesktopInstance?>? findInstance = null)
    {
        _store = store;
        _stillServes = stillServes ?? PowerBiDesktopDiscovery.StillServes;
        _isListening = isListening ?? PowerBiDesktopDiscovery.IsListening;
        _findInstance = findInstance ?? PowerBiDesktopDiscovery.FindInstance;
    }

    public TomixResult<ConnectShowResult> Show()
    {
        var state = _store.LoadCurrentSession();
        bool? reachable = null;
        string? lastReportName = null;

        if (state is { Model: null, Server: { } server } && ModelReference.IsLocalInstanceEndpoint(server))
        {
            // A Desktop session outlives the Desktop window: once the report is closed the saved
            // `localhost:<port>` and GUID database name are all that is left, and neither says
            // which report it was. Say the instance is gone, and keep the last-known name to
            // display so the user can tell what they were connected to.
            reachable = _isListening(server);
            if (reachable == false)
            {
                lastReportName = state.ReportName;
                state = state.WithoutReportCache();
            }
            else if (state.ReportName is null && _findInstance(server) is { } instance)
            {
                // Connected by endpoint rather than through the --local picker, so nothing was
                // cached. Look the report up now rather than show a bare port.
                state = state with { ReportName = instance.ReportName, ReportPortFile = instance.PortFile };
            }
        }

        // Drop a cached Desktop report name that no longer describes the live instance on that port —
        // Desktop may have restarted onto a different report, or exited and left its port file
        // behind — so no caller can display a stale name.
        if (state?.ReportName is not null && !_stillServes(state.ReportPortFile, state.Server))
            state = state.WithoutReportCache();

        return TomixResult<ConnectShowResult>.Ok(
            new ConnectShowResult(state is not null, state, reachable, lastReportName)
            {
                Session = new ConnectSessionInfo(
                    _store.CurrentSessionId, _store.CurrentSessionKind, _store.CurrentSessionScope, _store.CurrentSessionFile)
            });
    }

    /// <summary>
    /// Forgets the active connection. With <paramref name="all"/>, deletes every session file —
    /// the connection in every other repository, worktree, and named session too.
    /// </summary>
    public TomixResult<ConnectClearResult> Clear(bool all = false)
    {
        var existed = _store.LoadCurrentSession() is not null;
        if (!all)
        {
            _store.ClearCurrentSession();
            return TomixResult<ConnectClearResult>.Ok(new ConnectClearResult(existed));
        }

        return TomixResult<ConnectClearResult>.Ok(new ConnectClearResult(existed, _store.ClearAllSessions()));
    }

    public TomixResult<ConnectSetResult> Set(ConnectSetRequest request)
    {
        CliConnectionState state;
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            state = new CliConnectionState(
                null,
                request.Database,
                NormalizeLocalPath(request.Model),
                request.Auth,
                Local: true,
                Profile: request.Profile,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth);
        }
        else if (request.Local)
        {
            state = new CliConnectionState(
                // A Power BI Desktop instance is addressed by its discovered `localhost:<port>`
                // endpoint, so it has to survive here: with no Model and no Server the state says
                // "local" without naming a target, and ActiveModelResolver resolves it to nothing.
                // Anything that is not a local-instance endpoint is not a `--local` target.
                ModelReference.IsLocalInstanceEndpoint(request.Server) ? request.Server : null,
                request.Database,
                null,
                request.Auth,
                Local: true,
                Profile: request.Profile,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth,
                request.ReportName,
                request.ReportPortFile);
        }
        else
        {
            state = new CliConnectionState(
                request.Server,
                request.Database,
                null,
                request.Auth,
                Local: false,
                Profile: request.Profile,
                request.Workspace,
                request.WorkspaceFormat,
                request.WorkspaceAuth);
        }

        _store.SaveCurrentSession(state);
        _store.AddRecentConnection(state);

        // Connecting is when session files accumulate, so it is also where the provably dead ones
        // (deleted worktrees, exited legacy pid sessions) are swept.
        _store.PruneStaleSessions();
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
    string? WorkspaceAuth = null,
    /// <summary>Desktop report name to cache; see <c>CliConnectionState.ReportName</c>.</summary>
    string? ReportName = null,
    /// <summary>Port file the report name came from, used to revalidate it cheaply.</summary>
    string? ReportPortFile = null);
