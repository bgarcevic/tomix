namespace Tomix.App.Format;

public sealed class CompositeExpressionFormatterClient : IExpressionFormatterClient
{
    private readonly IReadOnlyList<IExpressionFormatterClient> _clients;

    public CompositeExpressionFormatterClient(IEnumerable<IExpressionFormatterClient> clients)
        => _clients = clients.ToList();

    public bool CanFormat(string language)
        => _clients.Any(client => client.CanFormat(language));

    public Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken)
    {
        var client = _clients.FirstOrDefault(candidate => candidate.CanFormat(request.Language));
        if (client is null)
        {
            return Task.FromResult(new ExpressionFormatResponse(
                false,
                request.Expression,
                [$"No formatter is available for language '{request.Language}'."]));
        }

        return client.FormatAsync(request, cancellationToken);
    }
}
