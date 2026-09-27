using Tomix.Core.Models;

namespace Tomix.App.Connect;

/// <summary>
/// Finds the database on a local Power BI Desktop instance. Desktop hosts exactly one database,
/// named by a GUID that the user never sees, so it can only be discovered, not typed.
/// </summary>
/// <remarks>
/// Resolving it matters because not every client auto-selects the only database: the TOM session
/// does, but VertiPaq extraction connects with a bare <c>Data Source</c> and fails without an
/// <c>Initial Catalog</c>.
/// </remarks>
public static class LocalInstanceDatabaseResolver
{
    /// <summary>
    /// The single database on <paramref name="endpoint"/>, or <c>null</c> when it is not a local
    /// instance, no provider can list it, the listing fails, or it does not hold exactly one
    /// database. Best-effort by design: callers fall back to connecting without a database.
    /// </summary>
    public static async Task<string?> TryResolveAsync(
        IEnumerable<IModelProvider> providers,
        string? endpoint,
        CancellationToken cancellationToken)
    {
        if (!ModelReference.IsLocalInstanceEndpoint(endpoint))
            return null;

        var reference = ModelReference.Remote(endpoint!);
        var catalog = providers.OfType<IServerCatalog>().FirstOrDefault(c => c.CanList(reference));
        return catalog is null ? null : await TryResolveAsync(catalog, reference, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string?> TryResolveAsync(
        IServerCatalog catalog,
        ModelReference endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var databases = await catalog.ListDatabasesAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return databases.Count == 1 && !string.IsNullOrWhiteSpace(databases[0].Name)
                ? databases[0].Name
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
