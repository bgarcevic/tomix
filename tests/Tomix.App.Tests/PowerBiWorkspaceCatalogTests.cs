using System.Net;
using Tomix.App.Connect;
using Tomix.Core.Authentication;

namespace Tomix.App.Tests;

public sealed class PowerBiWorkspaceCatalogTests
{
    private static readonly Uri Endpoint = new("https://api.powerbi.com/v1.0/myorg/groups");

    [Fact]
    public async Task ListWorkspaces_MapsFieldsAndCapacity()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "value": [
                { "id": "a1", "name": "Sales", "isOnDedicatedCapacity": true },
                { "id": "b2", "name": "Scratch", "isOnDedicatedCapacity": false }
            ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var workspaces = await catalog.ListWorkspacesAsync(CancellationToken.None);

        Assert.Collection(workspaces,
            w => { Assert.Equal("a1", w.Id); Assert.Equal("Sales", w.Name); Assert.True(w.IsOnDedicatedCapacity); },
            w => { Assert.Equal("Scratch", w.Name); Assert.False(w.IsOnDedicatedCapacity); });
    }

    [Fact]
    public async Task ListWorkspaces_SendsBearerTokenFromProvider()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "value": [] }"""));
        var provider = new FakeTokenProvider("secret-token");
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), provider, Endpoint);

        await catalog.ListWorkspacesAsync(CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization.Parameter);
        // The token is acquired for a powerbi:// endpoint so AuthScopes maps it to the Power BI scope.
        Assert.StartsWith("powerbi://", provider.RequestedEndpoint);
    }

    [Fact]
    public async Task ListWorkspaces_PagesUntilShortPage()
    {
        // pageSize 2: a full first page forces a second request; the short second page ends it.
        var responses = new Queue<string>(
        [
            """{ "value": [ { "id": "1", "name": "A" }, { "id": "2", "name": "B" } ] }""",
            """{ "value": [ { "id": "3", "name": "C" } ] }"""
        ]);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, responses.Dequeue()));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint, pageSize: 2);

        var workspaces = await catalog.ListWorkspacesAsync(CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C" }, workspaces.Select(w => w.Name));
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListWorkspaces_AuthFailure_ThrowsAuthenticationRequired(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json(status, """{ "error": "denied" }"""));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => catalog.ListWorkspacesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListWorkspaces_ServerError_ThrowsWithStatus()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "boom"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ListWorkspacesAsync(CancellationToken.None));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task ListWorkspaces_HttpTimeout_ReportsFailureNotCancellation()
    {
        // HttpClient's Timeout raises TaskCanceledException with the invocation token unset;
        // that must surface as an API failure, not propagate as a cancellation (exit 130).
        var handler = new ThrowingHandler(new TaskCanceledException("timeout"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ListWorkspacesAsync(CancellationToken.None));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task ListWorkspaces_UserCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ThrowingHandler(new TaskCanceledException("canceled"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => catalog.ListWorkspacesAsync(cts.Token));
    }

    [Fact]
    public async Task ListDatasets_MapsNames_AndTargetsTheWorkspace()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "value": [ { "id": "d1", "name": "Sales" }, { "id": "d2", "name": "Finance" } ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("a b/1", CancellationToken.None);

        Assert.Equal(new[] { "Sales", "Finance" }, datasets.Select(d => d.Name));
        // AbsoluteUri, not ToString(): the latter unescapes, hiding whether the id was encoded.
        Assert.Equal(
            "https://api.powerbi.com/v1.0/myorg/groups/a%20b%2F1/datasets",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        // The datasets endpoint returns the whole workspace: one request, no $top/$skip paging.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ListDatasets_SendsBearerTokenFromProvider()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "value": [] }"""));
        var provider = new FakeTokenProvider("secret-token");
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), provider, Endpoint);

        await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Equal("secret-token", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.StartsWith("powerbi://", provider.RequestedEndpoint);
    }

    [Fact]
    public async Task ListDatasets_SkipsPushDatasetsAndNamelessEntries()
    {
        // Push datasets are not addressable over XMLA, so the picker must not offer them.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "value": [
                { "id": "d1", "name": "Sales" },
                { "id": "d2", "name": "Realtime", "addRowsAPIEnabled": true },
                { "id": "d3" }
            ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Equal(new[] { "Sales" }, datasets.Select(d => d.Name));
    }

    // Excel/CSV uploads, usage-metrics models and streaming datasets are not served by the XMLA
    // endpoint either, and contentProviderType names them where addRowsAPIEnabled does not.
    [Theory]
    [InlineData("Excel")]
    [InlineData("CSV")]
    [InlineData("UsageMetricsUserReport")]
    [InlineData("UsageMetricsUserDashboard")]
    [InlineData("RealTimeInPushMode")]
    [InlineData("RealTimeInPubNubMode")]
    [InlineData("RealTimeInStreamingMode")]
    public async Task ListDatasets_SkipsNonXmlaContentProviders(string contentProviderType)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""
            { "value": [
                { "id": "d1", "name": "Sales", "contentProviderType": "PbixInImportMode" },
                { "id": "d2", "name": "Excluded", "contentProviderType": "{{contentProviderType}}" }
            ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Equal(new[] { "Sales" }, datasets.Select(d => d.Name));
    }

    // An unknown provider type must not be hidden: a real model missing from the picker is worse
    // than an unopenable one being offered, which fails with a clear diagnostic.
    [Fact]
    public async Task ListDatasets_UnknownContentProvider_IsKept()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "value": [ { "id": "d1", "name": "SomethingNew", "contentProviderType": "FabricFutureMode" } ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Equal(new[] { "SomethingNew" }, datasets.Select(d => d.Name));
    }

    // The REST reference spells this property with a leading capital while its neighbours are
    // camelCase, so the filter must not depend on which casing the service actually emits.
    [Theory]
    [InlineData("contentProviderType")]
    [InlineData("ContentProviderType")]
    public async Task ListDatasets_ContentProviderType_MatchedCaseInsensitively(string property)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""
            { "value": [ { "id": "d1", "name": "Excluded", "{{property}}": "RealTimeInPushMode" } ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Empty(datasets);
    }

    // Power BI returns full dataset content only to callers with Write permission; a Read-only
    // caller gets id and name alone. Nothing can be filtered then, and the listing must still work
    // rather than dropping every model or throwing.
    [Fact]
    public async Task ListDatasets_ReadOnlyPermissionShape_ListsEverything()
    {
        // Verbatim shape from the "Example with Read Only Permission" response in the REST docs.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "value": [
                { "id": "cfafbeb1-8037-4d0c-896e-a46fb27ff229", "name": "SalesMarketing" },
                { "id": "d2", "name": "Streaming" }
            ] }
            """));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var datasets = await catalog.ListDatasetsAsync("ws", CancellationToken.None);

        Assert.Equal(new[] { "SalesMarketing", "Streaming" }, datasets.Select(d => d.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListDatasets_AuthFailure_ThrowsAuthenticationRequired(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json(status, """{ "error": "denied" }"""));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => catalog.ListDatasetsAsync("ws", CancellationToken.None));
    }

    [Fact]
    public async Task ListDatasets_ServerError_NamesTheOperation()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "boom"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ListDatasetsAsync("ws", CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Contains("listing semantic models", ex.Message);
    }

    [Fact]
    public async Task ListDatasets_HttpTimeout_ReportsFailureNotCancellation()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("timeout"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ListDatasetsAsync("ws", CancellationToken.None));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task ListDatasets_UserCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ThrowingHandler(new TaskCanceledException("canceled"));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => catalog.ListDatasetsAsync("ws", cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListDatasets_BlankWorkspaceId_Rejected(string workspaceId)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "value": [] }"""));
        var catalog = new PowerBiWorkspaceCatalog(new HttpClient(handler), new FakeTokenProvider("tok"), Endpoint);

        // A blank id would otherwise GET /groups//datasets and 404 after a pointless round trip.
        await Assert.ThrowsAsync<ArgumentException>(
            () => catalog.ListDatasetsAsync(workspaceId, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeTokenProvider(string token) : IAccessTokenProvider
    {
        public string? RequestedEndpoint { get; private set; }

        public Task<AccessToken> GetTokenAsync(string endpoint, CancellationToken cancellationToken)
        {
            RequestedEndpoint = endpoint;
            return Task.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
