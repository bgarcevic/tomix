using Dax.Formatter;
using Dax.Formatter.Models;

namespace Tomix.App.Format;

public sealed class DaxFormatterApiClient : IExpressionFormatterClient
{
    private readonly DaxFormatterClient _client;

    public DaxFormatterApiClient()
        : this(new DaxFormatterClient("tomix-cli", "0.1.0"))
    {
    }

    internal DaxFormatterApiClient(DaxFormatterClient client) => _client = client;

    public bool CanFormat(string language)
        => string.Equals(language, FormatterLanguages.Dax, StringComparison.OrdinalIgnoreCase);

    public async Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var formatterRequest = new DaxFormatterSingleRequest
            {
                Dax = request.Expression,
                ListSeparator = request.Semicolons ? ';' : ',',
                DecimalSeparator = request.Semicolons ? ',' : '.',
                MaxLineLength = request.Long
                    ? DaxFormatterLineStyle.LongLine
                    : DaxFormatterLineStyle.ShortLine,
                SkipSpaceAfterFunctionName = request.NoSpaceAfterFunction
                    ? DaxFormatterSpacingStyle.NoSpaceAfterFunction
                    : DaxFormatterSpacingStyle.BestPractice
            };

            var response = await _client.FormatAsync(formatterRequest);
            var formatted = response?.Formatted ?? request.Expression;
            var errors = (response?.Errors ?? [])
                .Select(error => error.Message ?? "")
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();

            return new ExpressionFormatResponse(
                errors.Count == 0,
                formatted,
                errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExpressionFormatResponse(
                false,
                request.Expression,
                [ex.Message]);
        }
    }
}
