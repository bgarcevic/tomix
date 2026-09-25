namespace Tomix.Core.Diagnostics;

public sealed record TomixDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Hint = null,
    string? ObjectPath = null,
    string? File = null,
    int? Line = null,
    int? Column = null,
    bool? Blocked = null,
    string? Reason = null,
    int? NewValidationErrorCount = null,
    IReadOnlyList<ValidationErrorDetail>? NewErrors = null);

/// <summary>A validation error introduced by a mutation, without application-layer types.</summary>
public sealed record ValidationErrorDetail(string Code, string Message, string Object);
