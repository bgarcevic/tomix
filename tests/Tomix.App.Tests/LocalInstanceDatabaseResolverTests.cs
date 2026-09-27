using Tomix.App.Connect;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class LocalInstanceDatabaseResolverTests
{
    private const string DatabaseId = "0b4f5a1c-9e2d-4c5b-8a6f-3d2e1f0a9b8c";

    [Fact]
    public async Task SingleDatabase_IsReturned()
    {
        var provider = new CatalogProvider(DatabaseId);

        var database = await LocalInstanceDatabaseResolver.TryResolveAsync([provider], "localhost:56164", CancellationToken.None);

        Assert.Equal(DatabaseId, database);
        Assert.Equal("localhost:56164", provider.ListedEndpoint);
    }

    [Theory]
    [InlineData]
    [InlineData("a", "b")]
    public async Task NotExactlyOneDatabase_ReturnsNull(params string[] names)
        => Assert.Null(await LocalInstanceDatabaseResolver.TryResolveAsync(
            [new CatalogProvider(names)], "localhost:56164", CancellationToken.None));

    [Fact]
    public async Task ListingFailure_ReturnsNull()
        => Assert.Null(await LocalInstanceDatabaseResolver.TryResolveAsync(
            [new CatalogProvider(DatabaseId) { Failure = new InvalidOperationException("refused") }],
            "localhost:56164", CancellationToken.None));

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/ws")]
    [InlineData(null)]
    public async Task NonLocalEndpoint_IsNotListed(string? endpoint)
    {
        var provider = new CatalogProvider(DatabaseId);

        Assert.Null(await LocalInstanceDatabaseResolver.TryResolveAsync([provider], endpoint, CancellationToken.None));
        Assert.Null(provider.ListedEndpoint);
    }

    [Fact]
    public async Task NoCatalogProvider_ReturnsNull()
        => Assert.Null(await LocalInstanceDatabaseResolver.TryResolveAsync([], "localhost:56164", CancellationToken.None));

    /// <summary>A provider that can also list databases, like the TOM server provider.</summary>
    internal sealed class CatalogProvider(params string[] databases) : IModelProvider, IServerCatalog
    {
        public Exception? Failure { get; init; }
        public string? ListedEndpoint { get; private set; }

        public bool CanOpen(ModelReference reference) => false;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool CanList(ModelReference endpoint) => endpoint.IsRemote;

        public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(ModelReference endpoint, CancellationToken cancellationToken)
        {
            ListedEndpoint = endpoint.Value;
            return Failure is not null
                ? Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(Failure)
                : Task.FromResult<IReadOnlyList<ServerDatabaseInfo>>([.. databases.Select(n => new ServerDatabaseInfo(n))]);
        }
    }
}
