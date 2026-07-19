using Tomix.Core.Models;

namespace Tomix.App.State;

public sealed class ActiveModelResolver
{
    private readonly Func<CliConnectionState?> _loadSession;

    public ActiveModelResolver(CliStateStore store)
        : this(() => store.LoadCurrentSession())
    {
    }

    /// <summary>
    /// Resolves references against a caller-supplied session source (e.g. an injected
    /// <see cref="CliConnectionState"/> for tests) instead of reading the user state dir.
    /// </summary>
    public ActiveModelResolver(Func<CliConnectionState?> sessionSource)
        => _loadSession = sessionSource;

    public string Resolve(string? explicitModel)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
            return explicitModel;

        var sessionModel = _loadSession()?.Model;
        if (string.IsNullOrWhiteSpace(sessionModel))
            return "";

        if (!ModelReference.IsRemoteEndpoint(sessionModel) && !Path.IsPathRooted(sessionModel))
            return Path.GetFullPath(sessionModel);

        return sessionModel;
    }

    /// <summary>
    /// Resolves the model to open as a <see cref="ModelReference"/>. Precedence:
    /// <list type="number">
    ///   <item>An explicit <paramref name="explicitModel"/> (local path or remote endpoint).</item>
    ///   <item>An explicit <paramref name="server"/> (workspace name or endpoint; bare names are
    ///         expanded to their XMLA endpoint), paired with <paramref name="database"/>.
    ///         This overrides the active session.</item>
    ///   <item>The active session: a local workspace/model path, else a remote endpoint built
    ///         from the session server + database.</item>
    /// </list>
    /// An explicit <paramref name="database"/> applies to remote endpoints (the dataset/catalog)
    /// and overrides the session database.
    /// </summary>
    public ModelReference ResolveReference(string? explicitModel, string? database = null, string? server = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
            return ModelReference.IsRemoteEndpoint(explicitModel)
                ? new ModelReference(explicitModel, NullIfBlank(database))
                : new ModelReference(explicitModel);

        var session = _loadSession();

        // An explicit --server targets a model (endpoint or workspace name) directly and
        // overrides the active session. --database names the dataset/catalog on that server,
        // falling back to the session database when omitted. Bare workspace names are expanded
        // to a fully-qualified XMLA endpoint (matching connect); without this, no remote
        // provider can open the reference and the command fails with TOMIX_NO_PROVIDER.
        if (!string.IsNullOrWhiteSpace(server))
            return new ModelReference(
                ModelReference.NormalizeEndpoint(server),
                NullIfBlank(database) ?? session?.Database);

        if (session is null)
            return new ModelReference("");

        if (!string.IsNullOrWhiteSpace(session.Workspace)
            && !ModelReference.IsRemoteEndpoint(session.Workspace))
            return new ModelReference(session.Workspace);

        if (!string.IsNullOrWhiteSpace(session.Model))
        {
            var modelPath = session.Model;
            if (!ModelReference.IsRemoteEndpoint(modelPath) && !Path.IsPathRooted(modelPath))
                modelPath = Path.GetFullPath(modelPath);
            return new ModelReference(modelPath);
        }

        if (!string.IsNullOrWhiteSpace(session.Server))
            return new ModelReference(session.Server, NullIfBlank(database) ?? session.Database);

        return new ModelReference("");
    }

    public ModelReference? ResolveSyncTarget() => ResolveSyncTarget(_loadSession());

    /// <summary>
    /// Resolves the remote workspace synchronization target from an explicit connection snapshot.
    /// A remote workspace endpoint wins; otherwise the primary server is used when a local
    /// workspace is configured. This overload is the single policy used by command and mutation
    /// lifecycles.
    /// </summary>
    public static ModelReference? ResolveSyncTarget(CliConnectionState? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.Workspace))
            return null;

        if (ModelReference.IsRemoteEndpoint(session.Workspace))
            return new ModelReference(session.Workspace, NullIfBlank(session.Database));

        if (!string.IsNullOrWhiteSpace(session.Server))
            return new ModelReference(session.Server, NullIfBlank(session.Database));

        return null;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
