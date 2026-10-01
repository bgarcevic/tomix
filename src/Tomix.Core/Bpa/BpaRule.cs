using Tomix.Core.Rules;

namespace Tomix.Core.Bpa;

/// <summary>A Best-Practice-Analyzer rule: the shared <see cref="RuleDefinition"/> plus the minimum compatibility level it needs.</summary>
public sealed record BpaRule(
    string Id,
    string Name,
    string Category,
    RuleSeverity Severity,
    IReadOnlyList<string> Scope,
    string? Description = null,
    string? Expression = null,
    string? FixExpression = null,
    int CompatibilityLevel = 1200,
    string? DocsUrl = null)
    : RuleDefinition(Id, Name, Category, Severity, Scope, Description, Expression, FixExpression, DocsUrl);
