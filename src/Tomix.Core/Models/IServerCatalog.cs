namespace Tomix.Core.Models;

/// <summary>
/// Optional provider capability: enumerate the catalogs (semantic models) available on a
/// remote XMLA endpoint without opening a specific one. Implemented by providers that can
/// connect to a server-level endpoint (e.g. a Power BI workspace or Azure AS server).
/// </summary>
public interface IServerCatalog
{
    /// <summary>True when this provider can enumerate databases on the given endpoint.</summary>
    bool CanList(ModelReference endpoint);

    /// <summary>Lists the databases on the endpoint. <see cref="ModelReference.Database"/> is ignored.</summary>
    Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(
        ModelReference endpoint,
        CancellationToken cancellationToken);
}

/// <summary>A database (semantic model) visible on a remote endpoint.</summary>
public sealed record ServerDatabaseInfo(
    string Name,
    int? CompatibilityLevel = null,
    DateTimeOffset? LastUpdate = null);
