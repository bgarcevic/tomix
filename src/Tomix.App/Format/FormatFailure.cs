using System.Text.Json.Serialization;
using Tomix.Core.Diagnostics;
using Tomix.Core.Results;

namespace Tomix.App.Format;

/// <summary>
/// Why one expression failed to format, as a <c>tx format</c> sweep row reports it. The position
/// fields come from the first syntax error and are omitted for failures without one (an engine
/// timeout, or DAX the formatter declines).
/// </summary>
public sealed record FormatError(
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Stage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Line = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Column = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EndLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EndColumn = null)
{
    public static FormatError From(ExpressionFormatResponse response)
    {
        var message = FormatFailure.Detail(response);
        return response.SyntaxErrors.FirstOrDefault() is { } syntax
            ? new FormatError(
                message, syntax.Stage, syntax.Code, syntax.Line, syntax.Column, syntax.EndLine, syntax.EndColumn)
            : new FormatError(message);
    }
}

/// <summary>An object's expression could not be formatted; the mutation runner reports it as <c>TOMIX_FORMAT_FAILED</c>.</summary>
public sealed class ExpressionFormatFailedException(string objectPath, ExpressionFormatResponse response)
    : Exception($"Formatting failed for: {objectPath}: {FormatFailure.Detail(response)}")
{
    public string ObjectPath { get; } = objectPath;

    public IReadOnlyList<ExpressionSyntaxError> SyntaxErrors { get; } = response.SyntaxErrors;
}

internal static class FormatFailure
{
    public const string Code = "TOMIX_FORMAT_FAILED";

    public static string Detail(ExpressionFormatResponse response)
        => response.Errors.Count > 0
            ? string.Join("; ", response.Errors)
            : "the formatter reported a failure";

    /// <summary>
    /// A whole-model run where some expressions failed: nothing is applied, saved or staged. The
    /// result keeps its rows, adds a <c>TOMIX_FORMAT_FAILED</c> error and exits 1.
    /// </summary>
    public static TomixResult<T> NothingApplied<T>(TomixResult<T> result, int failed, int total)
        => result with
        {
            ExitCode = 1,
            Diagnostics =
            [
                .. result.Diagnostics,
                new TomixDiagnostic(
                    Code,
                    DiagnosticSeverity.Error,
                    $"No changes applied: {failed} of {total} expressions failed to format.",
                    Hint: "Fix the failed expressions and re-run; nothing is saved or staged until every expression formats.")
            ]
        };

    /// <summary>
    /// A <c>TOMIX_FORMAT_FAILED</c> result. The diagnostic's line and column are the first syntax
    /// error's, and every syntax error rides along so machine output can point at all of them.
    /// </summary>
    public static TomixResult<T> Result<T>(
        string message,
        IReadOnlyList<ExpressionSyntaxError> syntaxErrors,
        string? objectPath = null)
    {
        var first = syntaxErrors.FirstOrDefault();
        return new TomixResult<T>(
            Success: false,
            Data: default,
            Diagnostics:
            [
                new TomixDiagnostic(
                    Code,
                    DiagnosticSeverity.Error,
                    message,
                    ObjectPath: objectPath,
                    Line: first?.Line,
                    Column: first?.Column,
                    SyntaxErrors: syntaxErrors.Count > 0 ? syntaxErrors : null)
            ],
            ExitCode: 1);
    }
}
