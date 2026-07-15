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

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

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
