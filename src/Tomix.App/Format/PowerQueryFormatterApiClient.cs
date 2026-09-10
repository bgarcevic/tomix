using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tomix.App.Format;

public sealed class PowerQueryFormatterApiClient : IExpressionFormatterClient
{
    private static readonly Uri DefaultEndpoint = new("https://m-formatter.azurewebsites.net/api/v2");

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public PowerQueryFormatterApiClient(HttpClient httpClient)
        : this(httpClient, ResolveEndpoint())
    {
    }

    public PowerQueryFormatterApiClient(HttpClient httpClient, Uri endpoint)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
    }

    public bool CanFormat(string language)
        => string.Equals(language, FormatterLanguages.PowerQuery, StringComparison.OrdinalIgnoreCase);

    public async Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            code = request.Expression,
            resultType = "text",
            lineWidth = request.Long ? 120 : 40,
            alignLineCommentsToPosition = true,
            includeComments = true
        };

        try
        {
            // The service matches the Content-Type exactly and rejects the "charset=utf-8"
            // suffix that PostAsJsonAsync appends with HTTP 415.
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                new MediaTypeHeaderValue("application/json"));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new ExpressionFormatResponse(
                    false,
                    request.Expression,
                    [$"Power Query formatter returned HTTP {(int)response.StatusCode}: {body}"]);
            }

            return ParseResponse(body, request.Expression);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation (Ctrl-C): propagate to the exit-130 path.
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient's Timeout also surfaces as TaskCanceledException; without the
            // invocation token set, this is an endpoint timeout — a formatter failure,
            // not a user interrupt.
            return new ExpressionFormatResponse(
                false,
                request.Expression,
                [$"Power Query formatter request timed out after {_httpClient.Timeout.TotalSeconds:0} seconds."]);
        }
        catch (Exception ex)
        {
            return new ExpressionFormatResponse(
                false,
                request.Expression,
                [ex.Message]);
        }
    }

    private static ExpressionFormatResponse ParseResponse(string body, string original)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var success = !root.TryGetProperty("success", out var successProperty) ||
                      successProperty.ValueKind != JsonValueKind.False;

        var formatted = root.TryGetProperty("result", out var result)
            ? result.GetString() ?? original
            : original;

        var errors = ReadErrors(root).ToList();
        return new ExpressionFormatResponse(success, formatted, errors);
    }

    private static IEnumerable<string> ReadErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind == JsonValueKind.String)
                    yield return error.GetString() ?? "";
                else
                    yield return error.ToString();
            }
        }

        if (root.TryGetProperty("error", out var errorProperty))
            yield return errorProperty.ValueKind == JsonValueKind.String
                ? errorProperty.GetString() ?? ""
                : errorProperty.ToString();
    }

    private static Uri ResolveEndpoint()
    {
        var overrideValue = Environment.GetEnvironmentVariable("TOMIX_POWERQUERY_FORMATTER_API");
        return Uri.TryCreate(overrideValue, UriKind.Absolute, out var uri)
            ? uri
            : DefaultEndpoint;
    }
}
