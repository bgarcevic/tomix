using Tomix.Core.Dax;

namespace Tomix.App.Format;

/// <summary>
/// Formats DAX entirely offline through the vendored engine behind <see cref="DaxFormatter"/>:
/// no network, no rate limits, identical behavior air-gapped. Expressions with DAX syntax
/// errors are rejected with the error's line and message instead of being formatted, and when
/// the formatter declines because printing would change the code, the response says where the
/// first difference is.
/// </summary>
public sealed class OfflineDaxFormatterClient : IExpressionFormatterClient
{
    public bool CanFormat(string language)
        => string.Equals(language, FormatterLanguages.Dax, StringComparison.OrdinalIgnoreCase);

    public Task<ExpressionFormatResponse> FormatAsync(
        ExpressionFormatRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var syntaxIssues = DaxSyntaxCheck.Analyze(request.Expression);
        if (syntaxIssues.Count > 0)
        {
            return Task.FromResult(new ExpressionFormatResponse(
                false,
                request.Expression,
                [.. syntaxIssues.Select(issue =>
                    $"DAX syntax error on line {LineOf(request.Expression, issue.Start)}: {issue.Message}")]));
        }

        var result = DaxFormatter.TryFormat(request.Expression);
        if (!result.Success)
        {
            var detail = result.FirstDifferenceLine is { } line
                ? $" The first difference is on line {line}."
                : "";
            return Task.FromResult(new ExpressionFormatResponse(
                false,
                request.Expression,
                [$"The offline DAX formatter could not reformat this expression without changing its code.{detail}"]));
        }

        return Task.FromResult(new ExpressionFormatResponse(true, result.Formatted, []));
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }
}
