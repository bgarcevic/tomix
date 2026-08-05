using Tomix.App.Connect;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>Test double: returns a fixed set of workspaces (empty by default) and their datasets.</summary>
internal sealed class FakeWorkspaceCatalog : IWorkspaceCatalog
{
    public static readonly FakeWorkspaceCatalog Empty = new();

    private readonly IReadOnlyList<WorkspaceInfo> _workspaces;

    public FakeWorkspaceCatalog(params WorkspaceInfo[] workspaces) => _workspaces = workspaces;

    /// <summary>Datasets returned by <see cref="ListDatasetsAsync"/>, keyed by workspace id.</summary>
    public Dictionary<string, IReadOnlyList<string>> Datasets { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="ListDatasetsAsync"/> throws it (simulates a REST failure).</summary>
    public Exception? DatasetFailure { get; init; }

    /// <summary>Workspace ids passed to <see cref="ListDatasetsAsync"/>, in call order.</summary>
    public List<string> DatasetRequests { get; } = [];

    public Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken)
        => Task.FromResult(_workspaces);

    public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatasetsAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        DatasetRequests.Add(workspaceId);
        if (DatasetFailure is not null)
            return Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(DatasetFailure);

        IReadOnlyList<string> names = Datasets.TryGetValue(workspaceId, out var found) ? found : [];
        return Task.FromResult<IReadOnlyList<ServerDatabaseInfo>>(
            names.Select(n => new ServerDatabaseInfo(n)).ToList());
    }
}
