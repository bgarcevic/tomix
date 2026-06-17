using Tomix.Core.Models;

namespace Tomix.App.State;

public sealed class ActiveModelResolver
{
    private readonly CliStateStore _store;

    public ActiveModelResolver()
        : this(new CliStateStore())
    {
    }

    public ActiveModelResolver(CliStateStore store) => _store = store;

    public string Resolve(string? explicitModel)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
            return explicitModel;

        var sessionModel = _store.LoadCurrentSession()?.Model;
        if (string.IsNullOrWhiteSpace(sessionModel))
            return "";

        if (!ModelReference.IsRemoteEndpoint(sessionModel) && !Path.IsPathRooted(sessionModel))
            return Path.GetFullPath(sessionModel);

        return sessionModel;
    }

    /// <summary>
    /// Resolves the model to open as a <see cref="ModelReference"/>. An explicit value is taken
    /// verbatim (local path or remote endpoint). Otherwise the active session is used: a local
    /// model path if present, else a remote endpoint built from the session server + database.
    /// An explicit <paramref name="database"/> applies to remote endpoints (the dataset/catalog)
    /// and overrides the session database.
    /// </summary>
    public ModelReference ResolveReference(string? explicitModel, string? database = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
            return ModelReference.IsRemoteEndpoint(explicitModel)
                ? new ModelReference(explicitModel, NullIfBlank(database))
                : new ModelReference(explicitModel);

        var session = _store.LoadCurrentSession();
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

    public ModelReference? ResolveSyncTarget()
    {
        var session = _store.LoadCurrentSession();
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
