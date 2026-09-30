using Tomix.App.Mutations;
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
    IReadOnlyList<string>? RuleLoadDiagnostics = null)
{
    /// <summary>
    /// How <c>--fix</c> edits were persisted: unchanged when nothing was fixed, a preview when
    /// fixes were applied in memory only, otherwise the saved/staged lifecycle outcome.
    /// </summary>
    public MutationOutcome FixOutcome { get; init; } = MutationOutcome.Unchanged;

    /// <summary>
    /// Every rule source the run loaded, in load order, with the number of effective rules each
    /// contributed (#233). Feeds the "Rules loaded" attribution line and the JSON <c>ruleSources</c>.
    /// </summary>
    public IReadOnlyList<BpaRuleSourceSummary> RuleSources { get; init; } = [];

    /// <summary>
    /// Visible violations after <c>--fix</c> edits were applied, from a second evaluation of the
    /// mutated model; null when no fix was applied. <see cref="Violations"/> stays the pre-fix list.
    /// </summary>
    public IReadOnlyList<BpaViolation>? RemainingViolations { get; init; }

    /// <summary>
    /// The findings gates and exit codes judge: what remains after fixes, otherwise the run's
    /// violations — so a run that fixed every blocking finding does not fail (#297).
    /// </summary>
    public IReadOnlyList<BpaViolation> BlockingCandidates => RemainingViolations ?? Violations;

    /// <summary>
    /// Each fix <c>--fix</c> applied, or in a preview would apply, with before/after
    /// values for property sets. Empty when no fix ran.
    /// </summary>
    public IReadOnlyList<BpaFixChange> FixChanges { get; init; } = [];

    /// <summary>
    /// <c>--fix</c> without <c>--save</c>/<c>--stage</c>: the fixes were evaluated on an in-memory copy and discarded.
    /// <see cref="FixChanges"/> lists them, <see cref="FixesApplied"/> stays 0, and
    /// <see cref="ProjectedViolations"/> holds what the fixes would leave. The exit code judges
    /// the model as it is, because nothing changed.
    /// </summary>
    public bool Preview { get; init; }

    /// <summary>Visible violations the pending fixes would leave; set only under <see cref="Preview"/>.</summary>
    public IReadOnlyList<BpaViolation>? ProjectedViolations { get; init; }

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

    /// <summary>Rules skipped because the current user ignores them (<c>bpa rules ignore --user</c>).</summary>
    public IReadOnlyList<string> UserIgnoredRules => SuppressedRuleIds(BpaRuleSuppression.User);

    /// <summary>Rules skipped because the model's ignore annotation lists them (<c>bpa rules ignore</c>).</summary>
    public IReadOnlyList<string> ModelIgnoredRules => SuppressedRuleIds(BpaRuleSuppression.Model);

    private IReadOnlyList<string> SuppressedRuleIds(BpaRuleSuppression level)
        => Results
            .Where(r => r.Kind == BpaResultKind.DisabledRule && r.SuppressedBy.HasFlag(level))
            .Select(r => r.RuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Number of rules skipped because the model compatibility level is too low.</summary>
    public int InvalidCompatibilityRules => Results.Count(r => r.Kind == BpaResultKind.InvalidCompatibilityLevel);

    /// <summary>
    /// Rules skipped because they read <c>Vertipaq_*</c> statistics the model does not have.
    /// Not violations — missing statistics are the normal state of a fresh model — but always
    /// named, so a statistics rule never passes silently.
    /// </summary>
    public IReadOnlyList<BpaResult> MissingVertipaqStatsRules { get; } =
        Results.Where(r => r.Kind == BpaResultKind.MissingVertipaqStats).ToList();

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
