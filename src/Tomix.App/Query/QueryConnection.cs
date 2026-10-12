using Tomix.Core.Models;

namespace Tomix.App.Query;

/// <summary>
/// A query-capable connection to a live model, shared by the handlers that run DAX
/// (<c>query</c>, <c>test</c>). A caller who can read the model's metadata gets the full session;
/// one with read (Build) permission only gets a query-only session and no metadata.
/// </summary>
internal sealed class QueryConnection : IAsyncDisposable
{
    private QueryConnection(IModelQuerySession queries, IModelSession? session)
    {
        Queries = queries;
        Session = session;
    }

    /// <summary>Runs the queries.</summary>
    public IModelQuerySession Queries { get; }

    /// <summary>The full session, for reading the model's metadata; null with read access only.</summary>
    public IModelSession? Session { get; }

    /// <summary>
    /// Opens <paramref name="target"/>. Returns null when the provider's session cannot run
    /// queries, so each handler reports that with its own code. Connection failures throw as
    /// from <see cref="IModelProvider.OpenAsync"/>.
    /// </summary>
    public static async Task<QueryConnection?> OpenAsync(
        IModelProvider provider,
        ModelReference target,
        CancellationToken cancellationToken)
    {
        IModelSession session;
        try
        {
            session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelConnectionException ex)
            when (ex.Kind == ModelConnectionFailureKind.MetadataUnavailable && provider is IQueryOnlyModelProvider queryOnly)
        {
            // Read (Build) permission only: the model answers queries but its metadata is closed.
            return new QueryConnection(queryOnly.OpenQueryOnly(target), session: null);
        }

        if (session is IModelQuerySession queries)
            return new QueryConnection(queries, session);

        await session.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public ValueTask DisposeAsync() => Session?.DisposeAsync() ?? ValueTask.CompletedTask;
}
