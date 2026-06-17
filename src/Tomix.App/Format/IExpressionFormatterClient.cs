namespace Tomix.App.Format;

public interface IExpressionFormatterClient
{
    bool CanFormat(string language);

    Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken);
}
