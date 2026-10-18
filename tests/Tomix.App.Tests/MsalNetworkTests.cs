using Tomix.Auth;

namespace Tomix.App.Tests;

/// <summary>
/// MSAL builds its own HTTP client unless given one, so it inherits .NET's one-address-at-a-time
/// connect and times out on a network whose IPv6 is advertised but unroutable.
/// </summary>
public sealed class MsalNetworkTests
{
    [Fact]
    public void HttpClientFactory_SharesOneClient()
    {
        var factory = new DualStackMsalHttpClientFactory();

        Assert.Same(factory.GetHttpClient(), factory.GetHttpClient());
    }
}
