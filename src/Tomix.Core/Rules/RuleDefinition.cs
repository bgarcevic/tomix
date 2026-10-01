namespace Tomix.Core.Rules;

/// <summary>
/// The one rule shape every analysis engine describes its rules with, so <c>validate</c>,
/// <c>bpa</c>, and future auto-fixes cannot drift apart.
/// </summary>
/// <param name="Id">Stable identifier: the issue code for built-in checks, the rule ID for BPA.</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Category">Grouping shown in listings, for example <c>DAX Expressions</c>.</param>
/// <param name="Severity">How serious a finding is.</param>
/// <param name="Scope">The object types the rule inspects, as BPA scope tokens (<c>Measure</c>, <c>CalculatedColumn</c>, ...).</param>
/// <param name="Description">What the rule checks and why.</param>
/// <param name="Expression">
/// The predicate for data-driven rules (a BPA Dynamic-LINQ expression); <see langword="null"/>
/// for rules implemented in code.
/// </param>
/// <param name="FixExpression">The fix to apply to a matching object, when the rule has one.</param>
/// <param name="DocsUrl">Where the rule is documented, when it has a page.</param>
public abstract record RuleDefinition(
    string Id,
    string Name,
    string Category,
    RuleSeverity Severity,
    IReadOnlyList<string> Scope,
    string? Description = null,
    string? Expression = null,
    string? FixExpression = null,
    string? DocsUrl = null);
