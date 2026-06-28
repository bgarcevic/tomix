using System.Text.Json.Serialization;
using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

/// <summary>
/// The outcome of a BPA run. <see cref="Results"/> is the raw stream (violations plus disabled /
/// invalid-compatibility / error sentinels and ignored violations); <see cref="Violations"/> is the
/// visible projection consumers display by default (matched, non-ignored objects).
/// </summary>
public sealed record BpaRunResult(
    IReadOnlyList<BpaResult> Results,
    string ModelName,
    int RulesEvaluated,
    long DurationMs = 0,
    int FixesApplied = 0,
    int FixesSkipped = 0,
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
    /// <summary>Visible violations: matched objects that are not suppressed by an object-level ignore.</summary>
    public IReadOnlyList<BpaViolation> Violations { get; } =
        Results
            .Where(r => r.Kind == BpaResultKind.Violation && !r.IsIgnored && r.Violation is not null)
            .Select(r => r.Violation!)
            .ToList();

    /// <summary>Number of compilation/evaluation error sentinels.</summary>
    public int RuleErrors => Results.Count(r => r.Kind is BpaResultKind.CompilationError or BpaResultKind.EvaluationError);

    /// <summary>Number of rules skipped because they are globally disabled.</summary>
    public int DisabledRules => Results.Count(r => r.Kind == BpaResultKind.DisabledRule);

    /// <summary>Number of rules skipped because the model compatibility level is too low.</summary>
    public int InvalidCompatibilityRules => Results.Count(r => r.Kind == BpaResultKind.InvalidCompatibilityLevel);

    /// <summary>Number of object-level violations suppressed by an ignore annotation.</summary>
    public int IgnoredViolations => Results.Count(r => r.Kind == BpaResultKind.Violation && r.IsIgnored);
}
