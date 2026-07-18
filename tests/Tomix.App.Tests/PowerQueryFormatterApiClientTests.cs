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
            new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, false, false, false),
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
                new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, false, false, false),
                cts.Token));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
