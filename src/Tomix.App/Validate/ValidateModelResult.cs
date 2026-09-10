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

public sealed record ValidationIssue(
    string Code,
    string Message,
    string ObjectName,
    string? Expression,
    // Render-only: the offending expression line's text (trimmed/truncated), highlighted in
    // human output. Always ignored in JSON so the serialized shape stays byte-identical.
    [property: JsonIgnore]
    string? ExpressionLine = null);
