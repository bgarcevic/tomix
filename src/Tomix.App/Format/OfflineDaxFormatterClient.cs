using Tomix.Core.Dax;
using Tomix.Core.Diagnostics;

namespace Tomix.App.Format;

/// <summary>
/// Formats DAX entirely offline through the vendored engine behind <see cref="DaxFormatter"/>:
/// no network, no rate limits, identical behavior air-gapped. Expressions with DAX syntax
/// errors are rejected with each error's position (structured, in the shape the M engine
/// reports) and message instead of being formatted, and when
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
            var errors = syntaxIssues.Select(issue => ToSyntaxError(request.Expression, issue)).ToList();
            return Task.FromResult(new ExpressionFormatResponse(
                false,
                request.Expression,
                [.. errors.Select(error => $"DAX syntax error on line {error.Line}, column {error.Column}: {error.Message}")])
            {
                SyntaxErrors = errors
            });
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

    // The end is the issue's last character (inclusive); an issue at the end of the text has only a start.
    private static ExpressionSyntaxError ToSyntaxError(string expression, DaxSyntaxIssue issue)
    {
        var (line, column) = SourcePosition.Of(expression, issue.Start);
        if (issue.Length == 0)
            return new ExpressionSyntaxError(issue.Stage, issue.Code, issue.Message, line, column);

        var (endLine, endColumn) = SourcePosition.Of(expression, issue.Start + issue.Length - 1);
        return new ExpressionSyntaxError(issue.Stage, issue.Code, issue.Message, line, column, endLine, endColumn);
    }
}
