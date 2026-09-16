using System.Text.Json.Serialization;

namespace Tomix.App.Validate;

public sealed record ValidateModelResult(
    string ModelName,
    bool Valid,
    long DurationMs,
    IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings,
    // Render-only: resolves bracket references to measures in the human expression line.
    // JSON keeps its pinned shape (ValidateJsonContractTests).
    [property: JsonIgnore] IReadOnlySet<string>? MeasureNames = null);

/// <summary>How serious one validation issue is; serialized as a string in JSON.</summary>
public enum ValidationSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string ObjectName,
    string? Expression,
    // Render-only: the offending expression line's text (trimmed/truncated), highlighted in
    // human output. Always ignored in JSON so the serialized shape stays byte-identical.
    [property: JsonIgnore]
    string? ExpressionLine = null);
