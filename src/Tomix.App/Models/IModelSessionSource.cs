using Tomix.Core.Models;

namespace Tomix.App.Models;

/// <summary>
/// Where the runners get their model session (ADR 0001 §5). Handlers never open or dispose a
/// session themselves, so the same handler serves the one-shot CLI
/// (<see cref="OneShotSessionSource"/>) and an open live session (<see cref="LiveSessionSource"/>).
/// </summary>
public interface IModelSessionSource
{
    /// <summary>
    /// True when leases share one open live session. Mutations then apply and commit instead of
    /// previewing, nothing saves unless asked, and staging is unavailable.
    /// </summary>
    bool IsLive { get; }

    /// <summary>A session on <paramref name="model"/> for one request.</summary>
    /// <exception cref="ModelSessionUnavailableException">The source cannot serve this model.</exception>
    Task<ModelSessionLease> LeaseAsync(ModelReference model, CancellationToken cancellationToken);
}

/// <summary>
/// One request's use of a session. Commit it when the request succeeded; dispose it in every
/// case. A one-shot lease disposes its session; a live lease ends its transaction, rolling back
/// whatever it did unless it was committed, and leaves the session open.
/// </summary>
public sealed class ModelSessionLease : IAsyncDisposable
{
    private readonly ILiveSessionLease? _live;

    private ModelSessionLease(IModelSession session, ILiveSessionLease? live)
    {
        Session = session;
        _live = live;
    }

    public IModelSession Session { get; }

    public static ModelSessionLease OneShot(IModelSession session) => new(session, null);

    public static ModelSessionLease Live(ILiveSessionLease lease) => new(lease.Session, lease);

    /// <summary>Keeps the request's changes in a live session; does nothing for a one-shot one.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_live is not null)
            await _live.CommitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _live?.DisposeAsync() ?? Session.DisposeAsync();
}

/// <summary>A session source cannot serve the requested model; the runners report it verbatim.</summary>
public sealed class ModelSessionUnavailableException(string code, string message, int exitCode, string? hint = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public int ExitCode { get; } = exitCode;

    public string? Hint { get; } = hint;
}
