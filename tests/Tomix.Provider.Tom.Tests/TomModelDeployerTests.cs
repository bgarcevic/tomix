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
        var request = new ModelDeployRequest("powerbi://api.powerbi.com/v1.0/myorg/W", "M", CreateOnly: false, Force: false);

        await Assert.ThrowsAsync<Tomix.Core.Authentication.AuthenticationRequiredException>(
            () => TomModelDeployer.DeployAsync(db, request, tokenProvider: null, CancellationToken.None));
    }

    /// <summary>
    /// A full deploy preserves nothing, so its script must be generated offline — no
    /// connection, no auth. Also locks the envelope: name pinned to the requested database,
    /// ID matching it for a new-database script.
    /// </summary>
    [Fact]
    public async Task GenerateScriptAsync_FullOptions_IsOfflineAndTargetsRequestedName()
    {
        var db = new Database { Name = "Source", ID = "source-id", Model = new Model { Name = "Model" } };
        var request = new ModelDeployRequest(
            "powerbi://api.powerbi.com/v1.0/myorg/W", "Prod", CreateOnly: false, Force: false,
            Options: ModelDeployOptions.Full);

        var script = await TomModelDeployer.GenerateScriptAsync(db, request, tokenProvider: null, CancellationToken.None);

        var parsed = System.Text.Json.Nodes.JsonNode.Parse(script)!;
        Assert.Equal("Prod", parsed["createOrReplace"]!["object"]!["database"]!.GetValue<string>());
        Assert.Equal("Prod", parsed["createOrReplace"]!["database"]!["name"]!.GetValue<string>());
        Assert.Equal("Prod", parsed["createOrReplace"]!["database"]!["id"]!.GetValue<string>());
    }

    /// <summary>
    /// Preserve-by-default options require reading the target, so scripting a remote model
    /// without auth must fail loudly rather than silently emitting a full-overwrite script.
    /// </summary>
    [Fact]
    public async Task GenerateScriptAsync_PreserveOptions_RemoteWithoutToken_RequiresAuthentication()
    {
        var db = new Database { Name = "Source", Model = new Model { Name = "Model" } };
        var request = new ModelDeployRequest(
            "powerbi://api.powerbi.com/v1.0/myorg/W", "Prod", CreateOnly: false, Force: false);

        await Assert.ThrowsAsync<Tomix.Core.Authentication.AuthenticationRequiredException>(
            () => TomModelDeployer.GenerateScriptAsync(db, request, tokenProvider: null, CancellationToken.None));
    }
}
