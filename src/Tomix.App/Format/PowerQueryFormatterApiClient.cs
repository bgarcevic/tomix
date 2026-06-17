using System.Net.Http.Json;
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
            using var response = await _httpClient.PostAsJsonAsync(_endpoint, payload, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
