namespace Tomix.Core.Bpa;

public sealed record BpaRule(
    string Id,
    string Name,
    string Category,
    BpaSeverity Severity,
    IReadOnlyList<string> Scope,
    string? Description = null,
    string? Expression = null,
    string? FixExpression = null,
    int CompatibilityLevel = 1200);
