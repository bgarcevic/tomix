using Tomix.Core.Models;

namespace Tomix.App.Models;

/// <summary>
/// An open live session: every lease is a transaction on it, and ending the lease never closes
/// the session (the host that opened it does). It serves only the model it was opened from.
/// </summary>
public sealed class LiveSessionSource(ILiveModelSession session, LiveLeaseOptions options) : IModelSessionSource
{
    public ILiveModelSession Session { get; } = session;

    public bool IsLive => true;

    public async Task<ModelSessionLease> LeaseAsync(ModelReference model, CancellationToken cancellationToken)
    {
        if (!SameModel(model, Session.Reference))
            throw new ModelSessionUnavailableException(
                "TOMIX_SESSION_MODEL_MISMATCH",
                $"The live session holds {Describe(Session.Reference)}, not {Describe(model)}.",
                exitCode: 2,
                "Leave the model out to use the session's model, or open a session on the other model.");

        return ModelSessionLease.Live(await Session.LeaseAsync(options, cancellationToken));
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
}
