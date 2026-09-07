namespace Tomix.App.Validate;

public sealed record ValidateModelResult(
    string ModelName,
    bool Valid,
    long DurationMs,
    IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings);

public sealed record ValidationIssue(
    string Code,
    string Message,
    string ObjectName,
    string? Expression);
