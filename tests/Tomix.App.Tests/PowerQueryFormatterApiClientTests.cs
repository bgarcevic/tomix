using System.Net;
using System.Text.Json;
using Tomix.App.Format;

namespace Tomix.App.Tests;

/// <summary>
/// HTTP timeouts (HttpClient's Timeout raising TaskCanceledException with the invocation token
/// unset) must be reported as formatter failures; only genuine user cancellation may propagate
/// to Program's exit-130 path.
/// </summary>
public sealed class PowerQueryFormatterApiClientTests
{
    private static readonly Uri Endpoint = new("https://formatter.example/api/v2");

    [Fact]
    public async Task Format_HttpTimeout_ReturnsFailureNotCancellation()
    {
        var client = new PowerQueryFormatterApiClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout"))), Endpoint);

        var response = await client.FormatAsync(
            new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, false),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("timed out", Assert.Single(response.Errors));
    }

    [Fact]
    public async Task Format_UserCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new PowerQueryFormatterApiClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException("canceled"))), Endpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FormatAsync(
                new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, false),
                cts.Token));
    }

    [Fact]
    public async Task Format_SendsJsonContentTypeWithoutCharset()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "success": true, "result": "x" }"""));
        var client = new PowerQueryFormatterApiClient(new HttpClient(handler), Endpoint);

        var response = await client.FormatAsync(
            new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, false),
            CancellationToken.None);

        Assert.True(response.Success);
        // The service matches the Content-Type exactly; "application/json; charset=utf-8"
        // (what PostAsJsonAsync sent) is rejected with HTTP 415.
        Assert.Equal("application/json", handler.LastContentType);
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 120)]
    public async Task Format_SendsExpectedPayload(bool preferLong, int expectedLineWidth)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "success": true, "result": "x" }"""));
        var client = new PowerQueryFormatterApiClient(new HttpClient(handler), Endpoint);

        await client.FormatAsync(
            new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, preferLong),
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal("let x = 1 in x", root.GetProperty("code").GetString());
        Assert.Equal("text", root.GetProperty("resultType").GetString());
        Assert.Equal(expectedLineWidth, root.GetProperty("lineWidth").GetInt32());
        Assert.True(root.GetProperty("alignLineCommentsToPosition").GetBoolean());
        Assert.True(root.GetProperty("includeComments").GetBoolean());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    // The client disposes the request after sending, so headers and body are captured inside
    // the handler while the content is still readable.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastContentType { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
