using Tomix.App.Format.M;

namespace Tomix.App.Format;

/// <summary>
/// Formats Power Query (M) entirely offline through Microsoft's powerquery-formatter, bundled into
/// <c>tx</c> and run in-process by <see cref="PowerQueryEngine"/>: no network, no Node, identical
/// behavior air-gapped. Expressions that do not lex or parse are rejected with the error's line
/// and column instead of being formatted.
/// </summary>
public sealed class OfflineMFormatterClient : IExpressionFormatterClient
{
    private const string Indentation = "    ";
    private const string Newline = "\n";
    private const int DefaultWidth = 40;
    private const int LongWidth = 120;

    // One engine per process, created on first use: the bundle is evaluated only when M is
    // actually formatted, and the cost is paid once however many expressions follow.
    private static readonly Lazy<PowerQueryEngine> SharedEngine = new(() => new PowerQueryEngine());

    private readonly PowerQueryEngine? _engine;

    public OfflineMFormatterClient()
    {
    }

    internal OfflineMFormatterClient(PowerQueryEngine engine) => _engine = engine;

    public bool CanFormat(string language)
        => string.Equals(language, FormatterLanguages.PowerQuery, StringComparison.OrdinalIgnoreCase);

    public async Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = _engine ?? SharedEngine.Value;
        var result = await engine.FormatAsync(
            request.Expression,
            new PowerQueryFormatOptions(Indentation, Newline, request.Long ? LongWidth : DefaultWidth),
            cancellationToken);

        if (!result.Ok)
            return new ExpressionFormatResponse(false, request.Expression, [Describe(result.Error!)]);

        // The formatter ends its output with a newline; stored expressions do not, and keeping it
        // would make every already-formatted expression look changed.
        var formatted = result.Text!.EndsWith(Newline, StringComparison.Ordinal)
            ? result.Text[..^Newline.Length]
            : result.Text;
        return new ExpressionFormatResponse(true, formatted, []);
    }

    private static string Describe(PowerQueryEngineError error)
    {
        if (error.Kind == PowerQueryErrorKind.Internal)
            return error.Message;

        return error is { Line: { } line, Column: { } column }
            ? $"M syntax error on line {line}, column {column}: {error.Message}"
            : $"M syntax error: {error.Message}";
    }
}
