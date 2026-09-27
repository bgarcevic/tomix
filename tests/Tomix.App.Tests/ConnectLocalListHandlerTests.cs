using Tomix.App.Connect;

namespace Tomix.App.Tests;

public sealed class ConnectLocalListHandlerTests
{
    [Fact]
    public async Task ListsEveryInstance_WithReportNameAndDatabase()
    {
        var handler = new ConnectLocalListHandler(
            [new LocalInstanceDatabaseResolverTests.CatalogProvider("db-guid")],
            () =>
            [
                new PowerBiDesktopInstance("localhost:56164", "Revenue Opportunities", "a.txt"),
                new PowerBiDesktopInstance("localhost:51000", null, "b.txt")
            ]);

        var result = await handler.HandleAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            [
                new LocalInstanceInfo("localhost:56164", "Revenue Opportunities", "db-guid"),
                new LocalInstanceInfo("localhost:51000", null, "db-guid")
            ],
            result.Data!.Instances);
    }

    [Fact]
    public async Task NoInstances_IsAnEmptySuccess()
    {
        var result = await new ConnectLocalListHandler([], () => []).HandleAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Instances);
    }
}
