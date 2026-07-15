using Tomix.Core.Models;

namespace Tomix.App.Connect;

/// <summary>Lists the Power BI workspaces visible to the signed-in identity.</summary>
public interface IWorkspaceCatalog
{
    Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken);
}

/// <summary>A Power BI workspace as reported by the REST API.</summary>
public sealed record WorkspaceInfo(string Id, string Name, bool IsOnDedicatedCapacity)
{
    /// <summary>The workspace's XMLA endpoint (<c>powerbi://api.powerbi.com/v1.0/myorg/&lt;name&gt;</c>).</summary>
    public string XmlaEndpoint => ModelReference.NormalizeEndpoint(Name);
}
