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

            var response = await _client.FormatAsync(formatterRequest, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation (Ctrl-C): propagate to the exit-130 path.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The library's internal HttpClient timeout also surfaces as
            // TaskCanceledException; report it as a formatter failure, not an interrupt.
            return new ExpressionFormatResponse(
                false,
                request.Expression,
                ["DAX formatter request timed out."]);
        }
        catch (Exception ex)
        {
            return new ExpressionFormatResponse(
                false,
                request.Expression,
                [ex.Message]);
        }
    }
}
