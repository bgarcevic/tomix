using Tomix.App.Format.M;
using Tomix.App.M;
using Tomix.App.ModelObjects;
using Tomix.Core.Models;

namespace Tomix.App.Validate;

/// <summary>
/// The <c>validate</c> check for Power Query (M): every M partition source and shared expression
/// (see <see cref="MExpressions"/>) is lexed and parsed by the offline engine, and each error
/// becomes an <see cref="ValidationRules.MSyntax"/> issue. Kept apart from
/// <see cref="ModelValidation"/>, which stays synchronous for the mutation commands' error count;
/// only <c>validate</c> pays for the engine, and only when the model has M at all.
/// </summary>
internal static class MSyntaxValidation
{
    internal static async Task<IReadOnlyList<ValidationIssue>> AnalyzeAsync(
        ModelSnapshot snapshot,
        PowerQueryEngine engine,
        CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        foreach (var obj in ModelObjectProjection.Flatten(snapshot))
        {
            if (!MExpressions.IsMExpression(obj.Kind, obj.Detail) || string.IsNullOrWhiteSpace(obj.Expression))
                continue;

            var result = await engine.DiagnoseAsync(obj.Expression, cancellationToken);

            // An internal failure (a timeout, an engine that failed to load) says nothing about
            // the M itself, so it is not a finding about the model.
            foreach (var error in result.Errors.Where(error => error.Kind != PowerQueryErrorKind.Internal))
                issues.Add(ValidationRules.MSyntax.Issue(
                    Message(error),
                    obj.Path,
                    error.Line?.ToString(),
                    LineText(obj.Expression, error.Line)));
        }

        return issues;
    }

    private static string Message(PowerQueryEngineError error)
        => error.Column is { } column
            ? $"{error.Message} (column {column})"
            : error.Message;

    /// <summary>Very long offending lines are truncated, matching the DAX checks.</summary>
    private const int MaxExpressionLine = 120;

    private static string? LineText(string expression, int? line)
    {
        if (line is not { } number)
            return null;

        var lines = expression.Split('\n');
        if (number < 1 || number > lines.Length)
            return null;

        var text = lines[number - 1].TrimEnd();
        return text.Length > MaxExpressionLine ? text[..(MaxExpressionLine - 3)] + "..." : text;
    }
}
