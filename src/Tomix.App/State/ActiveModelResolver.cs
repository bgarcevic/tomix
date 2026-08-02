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
    /// Resolves the sync target for a save/mutation of <paramref name="effectiveModel"/>: the
    /// session's mirror applies only when the model being persisted is the session's primary
    /// model (what the session resolves to with no explicit source). A model addressed with an
    /// explicit path or --server/--database that resolves elsewhere is not an edit to the
    /// primary, so it must never be deployed over the session's workspace mirror.
    /// </summary>
    public ModelReference? ResolveSyncTarget(ModelReference effectiveModel)
        => ResolveSyncTarget(_loadSession(), effectiveModel);

    /// <summary>
    /// Model-aware overload of <see cref="ResolveSyncTarget(CliConnectionState?)"/> for callers
    /// holding an explicit connection snapshot; same primary-model gate as the instance overload.
    /// </summary>
    public static ModelReference? ResolveSyncTarget(CliConnectionState? session, ModelReference effectiveModel)
    {
        var target = ResolveSyncTarget(session);
        if (target is null)
            return null;

        var primary = new ActiveModelResolver(() => session).ResolveReference(null);
        return SameModel(primary, effectiveModel) ? target : null;
    }

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

    private static bool SameModel(ModelReference a, ModelReference b)
    {
        if (string.IsNullOrWhiteSpace(a.Value) || string.IsNullOrWhiteSpace(b.Value))
            return false;

        if (a.IsRemote != b.IsRemote)
            return false;

        if (a.IsRemote)
            return string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase);

        return SamePath(a.Value, b.Value);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(CanonicalPath(a), CanonicalPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Full-path normalization plus component-wise symlink resolution (realpath): the primary
    /// addressed through a link alias (e.g. <c>/tmp</c> vs <c>/private/tmp</c> on macOS) must
    /// still compare equal, otherwise an in-place save through the alias would silently skip
    /// the workspace sync. Components that do not exist or cannot be inspected stay textual.
    /// </summary>
    private static string CanonicalPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // Links can appear at any depth and their targets can contain further links
        // (e.g. an alias pointing into /var, itself a link to /private/var), so re-walk
        // until a pass resolves nothing — capped to break link cycles.
        for (var pass = 0; pass < 8; pass++)
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return full;

            var current = root;
            var resolvedAny = false;
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    {
                        current = target.FullName;
                        resolvedAny = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the textual component when the link target cannot be inspected.
                }
            }

            full = Path.TrimEndingDirectorySeparator(current);
            if (!resolvedAny)
                break;
        }

        return full;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
