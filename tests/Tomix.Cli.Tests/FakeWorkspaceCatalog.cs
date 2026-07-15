using Tomix.App.Connect;

namespace Tomix.Cli.Tests;

/// <summary>Test double: returns a fixed set of workspaces (empty by default).</summary>
internal sealed class FakeWorkspaceCatalog : IWorkspaceCatalog
{
    public static readonly FakeWorkspaceCatalog Empty = new();

    private readonly IReadOnlyList<WorkspaceInfo> _workspaces;

    public FakeWorkspaceCatalog(params WorkspaceInfo[] workspaces) => _workspaces = workspaces;

    public Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken)
        => Task.FromResult(_workspaces);
}
