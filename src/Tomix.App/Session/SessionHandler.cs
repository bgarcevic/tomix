using Tomix.App.State;
using Tomix.Core.Results;

namespace Tomix.App.Session;

public sealed class SessionHandler
{
    private readonly CliStateStore _store;

    public SessionHandler(CliStateStore store) => _store = store;

    public TomixResult<SessionShowResult> Show()
    {
        var state = _store.LoadCurrentSession();
        return TomixResult<SessionShowResult>.Ok(new SessionShowResult(
            _store.CurrentSessionId,
            _store.CurrentSessionKind,
            _store.CurrentSessionFile,
            state is not null,
            state));
    }

    public TomixResult<SessionListResult> List()
        => TomixResult<SessionListResult>.Ok(new SessionListResult(_store.ListSessions()));

    public TomixResult<SessionClearResult> Clear()
    {
        var existed = _store.LoadCurrentSession() is not null;
        _store.ClearCurrentSession();
        return TomixResult<SessionClearResult>.Ok(new SessionClearResult(existed));
    }

    public TomixResult<SessionPruneResult> Prune(bool all, bool dryRun)
    {
        var candidates = _store.SelectPruneCandidates(all);
        if (dryRun)
            return TomixResult<SessionPruneResult>.Ok(new SessionPruneResult(candidates.Count, DryRun: true));

        return TomixResult<SessionPruneResult>.Ok(new SessionPruneResult(CliStateStore.PruneSessions(candidates), DryRun: false));
    }
}
