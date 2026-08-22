using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Pins the provider's connection-string seam. The timeout itself, and the rule that nothing
/// hand-builds a connection string, are covered by <c>XmlaConnectionStringTests</c> in
/// <c>Tomix.Core.Tests</c>; what matters here is that this seam keeps routing through the builder
/// and keeps scoping to the resolved catalog — <c>TomModelQueryExecutor</c> re-derives its ADOMD
/// connection from it so a single-database endpoint opened without <c>--database</c> still targets
/// the right catalog.
/// </summary>
public sealed class TomConnectionStringTests
{
    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales")]
    [InlineData("asazure://westeurope.asazure.windows.net/myserver")]
    public void RemoteReference_CarriesConnectTimeout(string endpoint)
        => Assert.Equal(
            XmlaConnectionString.Build(endpoint),
            TomServerModelProvider.BuildConnectionString(new ModelReference(endpoint, Database: null)));

    [Fact]
    public void LocalDesktopInstance_OmitsConnectTimeout()
        => Assert.DoesNotContain(
            "Connect Timeout",
            TomServerModelProvider.BuildConnectionString(new ModelReference("localhost:59962", Database: null)),
            StringComparison.Ordinal);

    [Fact]
    public void ResolvedDatabase_ScopesTheConnection()
    {
        var connectionString = TomServerModelProvider.BuildConnectionString(
            new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Sales", "SalesModel"));

        Assert.Contains("Initial Catalog=SalesModel", connectionString, StringComparison.Ordinal);
        Assert.Contains("Connect Timeout=30", connectionString, StringComparison.Ordinal);
    }
}
