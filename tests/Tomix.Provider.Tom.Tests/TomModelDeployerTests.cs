using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

public sealed class TomModelDeployerTests
{
    /// <summary>
    /// DeployAsync owns its server via try/finally. A pre-connect failure (here: missing
    /// auth for a remote endpoint) must surface the original error, not an
    /// ObjectDisposedException from cleaning up the never-connected server. Deliberately
    /// avoids any live connection attempt so the test stays fast and deterministic.
    /// </summary>
    [Fact]
    public async Task DeployAsync_RemoteWithoutToken_RequiresAuthentication()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        var request = new ModelDeployRequest("powerbi://api.powerbi.com/v1.0/myorg/W", "M", DeployFull: true, CreateOnly: false, Force: false);

        await Assert.ThrowsAsync<Tomix.Core.Authentication.AuthenticationRequiredException>(
            () => TomModelDeployer.DeployAsync(db, request, tokenProvider: null, CancellationToken.None));
    }
}
