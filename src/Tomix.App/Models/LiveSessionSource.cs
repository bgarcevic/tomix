using Tomix.Core.Models;

namespace Tomix.App.Models;

/// <summary>
/// An open live session: every lease is a transaction on it, and ending the lease never closes
/// the session (the host that opened it does). It serves only the model it was opened from.
/// </summary>
public sealed class LiveSessionSource(ILiveModelSession session, LiveLeaseOptions options) : IModelSessionSource
{
    public ILiveModelSession Session { get; } = session;

    /// <summary>The options of the next leases; a host that labels each request sets them per request.</summary>
    public LiveLeaseOptions Options { get; set; } = options;

    public bool IsLive => true;

    /// <summary>
    /// When set, leases read this published snapshot instead of leasing the session (ADR 0001 §3):
    /// they wait for no other lease, see no uncommitted change, and cannot write. A host sets it
    /// for read-only analysis by a client that has no transaction open.
    /// </summary>
    public LiveModelSnapshot? Snapshot { get; set; }

    public async Task<ModelSessionLease> LeaseAsync(ModelReference model, CancellationToken cancellationToken)
    {
        if (!SameModel(model, Session.Reference))
            throw new ModelSessionUnavailableException(
                "TOMIX_SESSION_MODEL_MISMATCH",
                $"The live session holds {Describe(Session.Reference)}, not {Describe(model)}.",
                exitCode: 2,
                "Leave the model out to use the session's model, or open a session on the other model.");

        if (Snapshot is { } snapshot)
            return ModelSessionLease.OneShot(new SnapshotSession(Session, snapshot.Snapshot));
        return ModelSessionLease.Live(await Session.LeaseAsync(Options, cancellationToken));
    }

    internal static bool SameModel(ModelReference requested, ModelReference held)
    {
        if (requested.IsRemote || held.IsRemote)
            return requested.IsRemote && held.IsRemote
                && string.Equals(requested.Value.TrimEnd('/'), held.Value.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(requested.Database, held.Database, StringComparison.OrdinalIgnoreCase);

        return requested.IsLocalPath && held.IsLocalPath
            && string.Equals(FullPath(requested.Value), FullPath(held.Value), PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string FullPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Describe(ModelReference model)
        => model.Database is null ? $"'{model.Value}'" : $"'{model.Database}' on '{model.Value}'";

    /// <summary>A read-only view of one published snapshot; disposing it leaves the session open.</summary>
    private sealed class SnapshotSession(ILiveModelSession session, ModelSnapshot snapshot) : IModelSession
    {
        public string SourcePath => session.SourcePath;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken) => session.GetSummaryAsync(cancellationToken);

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
