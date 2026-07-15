using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>Test double for <see cref="IServerCatalog"/>: returns a fixed list of databases.</summary>
internal sealed class FakeServerCatalog : IServerCatalog
{
    private readonly IReadOnlyList<ServerDatabaseInfo> _databases;

    public FakeServerCatalog(params string[] databaseNames)
        => _databases = databaseNames.Select(n => new ServerDatabaseInfo(n)).ToList();

    /// <summary>When set, <see cref="ListDatabasesAsync"/> throws it (simulates auth/XMLA failure).</summary>
    public Exception? Failure { get; init; }

    public bool CanList(ModelReference endpoint) => true;

    public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(
        ModelReference endpoint, CancellationToken cancellationToken)
        => Failure is null ? Task.FromResult(_databases) : Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(Failure);
}
