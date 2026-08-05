using Tomix.App.Connect;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// How <c>connect</c> decides to enumerate the models on an endpoint. The picked workspace's REST
/// group id buys a single dataset call instead of an XMLA handshake, so these guards are about
/// wall-clock as much as correctness: an accidental fall-through to <see cref="IServerCatalog"/>
/// costs seconds per prompt and would otherwise go unnoticed.
/// </summary>
public sealed class ConnectModelListerTests
{
    private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Sales";

    private static ModelReference Reference => ModelReference.Remote(Endpoint);

    private static WorkspaceInfo Workspace(string id = "ws-1", string name = "Sales")
        => new(id, name, IsOnDedicatedCapacity: true);

    [Fact]
    public async Task PickedWorkspace_ListsOverRest_WithoutTouchingXmla()
    {
        var servers = new FakeServerCatalog("FromXmla");
        var workspaces = new FakeWorkspaceCatalog { Datasets = { ["ws-1"] = ["FromRest"] } };

        var listing = await ConnectCommand.BuildModelLister(
            Reference, Workspace(), servers, workspaces)(CancellationToken.None);

        Assert.Equal(new[] { "FromRest" }, listing.Models.Select(m => m.Name));
        Assert.Null(listing.Note);
        Assert.Equal(new[] { "ws-1" }, workspaces.DatasetRequests);
        Assert.Equal(0, servers.ListCount);
    }

    [Fact]
    public async Task NoWorkspace_ListsOverXmla()
    {
        var servers = new FakeServerCatalog("FromXmla");
        var workspaces = new FakeWorkspaceCatalog();

        var listing = await ConnectCommand.BuildModelLister(
            Reference, null, servers, workspaces)(CancellationToken.None);

        Assert.Equal(new[] { "FromXmla" }, listing.Models.Select(m => m.Name));
        Assert.Empty(workspaces.DatasetRequests);
        Assert.Equal(1, servers.ListCount);
    }

    // A workspace with no group id (nothing today produces one, but the record allows it) must not
    // send a REST call that can only 404.
    [Fact]
    public async Task WorkspaceWithoutId_ListsOverXmla()
    {
        var servers = new FakeServerCatalog("FromXmla");
        var workspaces = new FakeWorkspaceCatalog();

        var listing = await ConnectCommand.BuildModelLister(
            Reference, Workspace(id: ""), servers, workspaces)(CancellationToken.None);

        Assert.Equal(new[] { "FromXmla" }, listing.Models.Select(m => m.Name));
        Assert.Empty(workspaces.DatasetRequests);
    }

    [Fact]
    public async Task RestFailure_FallsBackToXmla_AndExplains()
    {
        var servers = new FakeServerCatalog("FromXmla");
        var workspaces = new FakeWorkspaceCatalog
        {
            DatasetFailure = new InvalidOperationException("Power BI API returned HTTP 403")
        };

        var listing = await ConnectCommand.BuildModelLister(
            Reference, Workspace(), servers, workspaces)(CancellationToken.None);

        Assert.Equal(new[] { "FromXmla" }, listing.Models.Select(m => m.Name));
        Assert.Contains("403", listing.Note);
        Assert.Equal(1, servers.ListCount);
    }

    // With no XMLA catalog there is nothing to fall back to, so the failure must reach the caller
    // (which renders TOMIX_REMOTE_LIST_FAILED) rather than becoming an empty model list.
    [Fact]
    public async Task RestFailure_WithoutXmlaCatalog_Rethrows()
    {
        var workspaces = new FakeWorkspaceCatalog { DatasetFailure = new InvalidOperationException("denied") };

        var lister = ConnectCommand.BuildModelLister(Reference, Workspace(), null, workspaces);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lister(CancellationToken.None));
    }

    // Cancellation must never be swallowed into a slow XMLA fallback — Ctrl-C has to stay exit 130.
    [Fact]
    public async Task Cancellation_IsNotTreatedAsRestFailure()
    {
        var servers = new FakeServerCatalog("FromXmla");
        var workspaces = new FakeWorkspaceCatalog { DatasetFailure = new OperationCanceledException() };

        var lister = ConnectCommand.BuildModelLister(Reference, Workspace(), servers, workspaces);

        await Assert.ThrowsAsync<OperationCanceledException>(() => lister(CancellationToken.None));
        Assert.Equal(0, servers.ListCount);
    }

    [Theory]
    [InlineData(Endpoint, true)]
    [InlineData("POWERBI://API.POWERBI.COM/V1.0/MYORG/SALES", true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Other", false)]
    public void WorkspaceFor_OnlyMatchesItsOwnEndpoint(string endpoint, bool expectMatch)
    {
        // PrimaryDatabase can be reached with a typed --server after a workspace was picked
        // earlier in the same run; that endpoint must not inherit the picked group id.
        var resolved = ConnectCommand.WorkspaceFor(Workspace(), endpoint);

        Assert.Equal(expectMatch, resolved is not null);
    }

    [Fact]
    public void WorkspaceFor_NothingPicked_IsNull()
        => Assert.Null(ConnectCommand.WorkspaceFor(null, Endpoint));
}
