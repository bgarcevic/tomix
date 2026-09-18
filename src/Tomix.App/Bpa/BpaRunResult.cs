using System.Text.Json.Serialization;
using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

/// <summary>
/// The outcome of a BPA run. <see cref="Results"/> is the raw stream (violations plus disabled /
/// invalid-compatibility / error sentinels and ignored violations); <see cref="Violations"/> is the
/// visible projection consumers display by default (matched, non-ignored objects) — plus one
/// error-severity finding per unevaluable rule, so gates fail closed.
/// </summary>
public sealed record BpaRunResult(
    IReadOnlyList<BpaResult> Results,
    string ModelName,
    int RulesEvaluated,
    long DurationMs = 0,
    int FixesApplied = 0,
    int FixesSkipped = 0,
    int DestructiveFixesSkipped = 0,
    IReadOnlyList<string>? FixErrors = null,
    object? Saved = null,
    bool? Staged = null,
    IReadOnlyList<string>? RuleLoadDiagnostics = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null)
{
    /// <summary>
    /// Unevaluable rules projected as error-severity findings ("rule could not be evaluated:
    /// reason"). A rule that cannot be compiled or evaluated is itself a finding — otherwise a
    /// typo would silently disable the rule for every consumer of the gate — regardless of the
    /// severity the broken rule itself declares. One finding per rule: the engine emits one
    /// sentinel per scope, so a rule that fails in several scopes must not inflate the count.
    /// </summary>
    public IReadOnlyList<BpaViolation> RuleErrorViolations { get; } = BuildRuleErrorViolations(Results);

    /// <summary>
    /// Visible violations: matched objects that are not suppressed by an object-level ignore,
    /// plus the rule-error findings. Blocking decisions (exit codes, gates) run on this
    /// projection, so an unevaluable rule fails <c>bpa run</c> and the deploy gate by default.
    /// </summary>
    public IReadOnlyList<BpaViolation> Violations { get; } = BuildViolations(Results);

    /// <summary>Number of compilation/evaluation error sentinels.</summary>
    public int RuleErrors => Results.Count(r => r.Kind is BpaResultKind.CompilationError or BpaResultKind.EvaluationError);

    /// <summary>Number of rules skipped because they are globally disabled.</summary>
    public int DisabledRules => Results.Count(r => r.Kind == BpaResultKind.DisabledRule);

    /// <summary>Number of rules skipped because the model compatibility level is too low.</summary>
    public int InvalidCompatibilityRules => Results.Count(r => r.Kind == BpaResultKind.InvalidCompatibilityLevel);

    /// <summary>Number of object-level violations suppressed by an ignore annotation.</summary>
    public int IgnoredViolations => Results.Count(r => r.Kind == BpaResultKind.Violation && r.IsIgnored);

    private static IReadOnlyList<BpaViolation> BuildViolations(IReadOnlyList<BpaResult> results)
        => results
            .Where(r => r.Kind == BpaResultKind.Violation && !r.IsIgnored && r.Violation is not null)
            .Select(r => r.Violation!)
            .Concat(BuildRuleErrorViolations(results))
            .ToList();

    private static IReadOnlyList<BpaViolation> BuildRuleErrorViolations(IReadOnlyList<BpaResult> results)
        => results
            .Where(r => r.Kind is BpaResultKind.CompilationError or BpaResultKind.EvaluationError)
            .GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
            .Select(ToRuleErrorFinding)
            .ToList();

    private static BpaViolation ToRuleErrorFinding(IGrouping<string, BpaResult> group)
    {
        var first = group.First();
        var reasons = group
            .Select(s => s.ErrorScope is null ? s.ErrorMessage : $"{s.ErrorScope}: {s.ErrorMessage}")
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BpaViolation(
            first.RuleId,
            first.RuleName,
            first.Category,
            BpaSeverity.Error,
            first.ErrorScope ?? "Rule",
            ObjectName: string.Empty,
            ObjectPath: string.Empty,
            Description: $"Rule could not be evaluated: {(reasons.Count > 0 ? string.Join("; ", reasons) : "no detail reported")}",
            CanFix: false);
    }
}
