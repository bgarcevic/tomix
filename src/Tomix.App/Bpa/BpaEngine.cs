using System.Text.RegularExpressions;
using Tomix.App.Bpa.Model;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Bpa;

/// <summary>
/// Evaluates Best-Practice-Analyzer rules against a model snapshot. Each rule's
/// <c>Expression</c> (a Dynamic-LINQ predicate) is evaluated generically against an
/// adapter object model (<see cref="BpaModel"/>) — there is no per-rule hardcoded logic, so any
/// rule expressed in the <c>bpa-rules.json</c> format is honored. A rule whose expression cannot
/// be evaluated against the available metadata is skipped (never a false positive).
/// </summary>
public sealed class BpaEngine
{
    private readonly BpaExpressionEvaluator _evaluator = new();

    public BpaRunResult Evaluate(ModelSnapshot snapshot, BpaEngineOptions options)
    {
        var ruleIds = options.RuleIds is { Count: > 0 }
            ? new HashSet<string>(options.RuleIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var activeRules = ruleIds is null
            ? options.Rules
            : options.Rules.Where(r => ruleIds.Contains(r.Id)).ToList();

        var model = BpaModelBuilder.Build(snapshot);

        // Rules disabled globally via the model-level ignore annotation, or by the user (machine-wide
        // disable), are not evaluated.
        var disabledRuleIds = new HashSet<string>(BpaIgnoreStore.ReadRuleIds(model.Source), StringComparer.OrdinalIgnoreCase);
        if (options.DisabledRuleIds is { Count: > 0 })
            disabledRuleIds.UnionWith(options.DisabledRuleIds);
        var results = new List<BpaResult>();

        foreach (var rule in activeRules)
            EvaluateRule(rule, model, snapshot, disabledRuleIds, options.PathFilter, results);

        return new BpaRunResult(results, snapshot.Name, activeRules.Count);
    }

    private void EvaluateRule(
        BpaRule rule,
        BpaModel model,
        ModelSnapshot snapshot,
        IReadOnlySet<string> disabledRuleIds,
        string? pathFilter,
        List<BpaResult> results)
    {
        // A globally disabled rule is recorded as a sentinel and never evaluated.
        if (disabledRuleIds.Contains(rule.Id))
        {
            results.Add(BpaResult.Sentinel(BpaResultKind.DisabledRule, rule));
            return;
        }

        // The model must meet the rule's minimum compatibility level, otherwise the rule's
        // metadata may not even exist on this model — emit a sentinel and do not evaluate.
        if (rule.CompatibilityLevel > snapshot.CompatibilityLevel)
        {
            results.Add(BpaResult.Sentinel(
                BpaResultKind.InvalidCompatibilityLevel,
                rule,
                $"Rule requires compatibility level {rule.CompatibilityLevel}; model is {snapshot.CompatibilityLevel}."));
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.Expression))
            return;

        // Evaluate the rule only over objects whose actual ObjectType is one of the rule's
        // scope tokens — e.g. a rule scoped to CalculatedColumn must not see DataColumns. Clean
        // matches are always emitted; a compile/eval failure is surfaced as a diagnostic sentinel
        // alongside them (the rule is not aborted — other scopes still run).
        void Eval<T>(string scopeLabel, IEnumerable<T> items) where T : BpaObject
        {
            var scoped = items.Where(o => MatchesScope(rule.Scope, o)).ToList();
            if (scoped.Count == 0)
                return;

            EmitOutcome(rule, scopeLabel, _evaluator.Evaluate(rule.Expression!, scoped), o => o.Source, pathFilter, results);
        }

        Eval("Measure", model.AllMeasures);
        Eval("Column", model.AllColumns);
        Eval("Table", model.Tables);
        Eval("Relationship", model.Relationships);
        Eval("Partition", model.AllPartitions);
        Eval("ModelRole", model.Roles);
        Eval("CalculationItem", model.AllCalculationItems);
        Eval("DataSource", model.DataSources);
        Eval("Perspective", model.Perspectives);
        Eval("Hierarchy", model.Hierarchies);

        if (rule.Scope.Any(s => Eq(s, "Model")))
            EmitOutcome(rule, "Model", _evaluator.Evaluate(rule.Expression!, new[] { model }), _ => model.Source, pathFilter, results);
    }

    /// <summary>
    /// Records the violations from a scope evaluation (each subject to the path filter) and, when the
    /// scope could not be fully evaluated, a single compile/eval-error sentinel for the rule.
    /// </summary>
    private static void EmitOutcome<T>(
        BpaRule rule,
        string scopeLabel,
        BpaEvaluation<T> outcome,
        Func<T, ModelObject> source,
        string? pathFilter,
        List<BpaResult> results)
        where T : class
    {
        foreach (var hit in outcome.Matches)
        {
            var obj = source(hit);
            if (pathFilter is not null && !MatchesPathFilter(obj, pathFilter))
                continue;

            // An object-level ignore annotation suppresses this object's violation of this rule
            // (kept in the raw stream, excluded from the visible violations) without disabling the
            // rule globally. Ignores are not inherited from parent objects.
            var ignored = BpaIgnoreStore.IsIgnored(obj, rule.Id);
            results.Add(BpaResult.ForViolation(rule, ToViolation(rule, obj), ignored));
        }

        if (outcome.Status != BpaEvaluationStatus.Ok)
            results.Add(BpaResult.Sentinel(
                outcome.Status == BpaEvaluationStatus.CompilationError
                    ? BpaResultKind.CompilationError
                    : BpaResultKind.EvaluationError,
                rule,
                outcome.ErrorMessage,
                scopeLabel));
    }

    private static bool MatchesScope(IReadOnlyList<string> scope, BpaObject obj)
    {
        foreach (var token in scope)
            if (ObjectInScope(obj, token))
                return true;
        return false;
    }

    /// <summary>Whether an adapter object's real type satisfies a single rule scope token.</summary>
    private static bool ObjectInScope(BpaObject obj, string token) => obj switch
    {
        BpaColumn => Eq(token, "Column") || Eq(token, obj.Source.Property("ObjectType") ?? "DataColumn"),
        BpaMeasure => Eq(token, "Measure") || (Eq(token, "KPI") && obj.Source.Property("KPI") == "Present"),
        // "Table" scope excludes calculated tables and calculation-group tables (spec §10):
        // those have their own dedicated scopes.
        BpaTable => (Eq(token, "Table") && !IsCalculatedTable(obj) && !IsCalculationGroup(obj))
            || (Eq(token, "CalculatedTable") && IsCalculatedTable(obj))
            || (Eq(token, "CalculationGroup") && IsCalculationGroup(obj)),
        BpaCalculationItem => Eq(token, "CalculationItem"),
        BpaDataSource ds => Eq(token, "DataSource")
            || (Eq(token, "StructuredDataSource") && Eq(ds.Type, "Structured"))
            || (Eq(token, "ProviderDataSource") && Eq(ds.Type, "Provider")),
        BpaPerspective => Eq(token, "Perspective"),
        BpaHierarchy => Eq(token, "Hierarchy"),
        BpaRelationship => Eq(token, "Relationship"),
        BpaPartition => Eq(token, "Partition"),
        BpaRole => Eq(token, "ModelRole"),
        _ => false
    };

    private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool IsCalculatedTable(BpaObject obj)
        => obj.Source.Property("TableIsCalc") == "true";

    private static bool IsCalculationGroup(BpaObject obj)
        => obj.Source.Property("TableObjectType") == "CalculationGroup";

    private static bool MatchesPathFilter(ModelObject obj, string pathFilter)
    {
        if (obj.Path.StartsWith(pathFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pathFilter.Contains('*'))
        {
            var pattern = "^" + Regex.Escape(pathFilter).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(obj.Path, pattern, RegexOptions.IgnoreCase);
        }

        var slashIndex = obj.Path.IndexOf('/');
        if (slashIndex > 0)
        {
            var tableName = obj.Path[..slashIndex];
            if (tableName.Equals(pathFilter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static BpaViolation ToViolation(BpaRule rule, ModelObject obj)
        => new(
            rule.Id,
            rule.Name,
            rule.Category,
            rule.Severity,
            ReferenceObjectType(obj),
            ReferenceObjectName(obj),
            obj.Path,
            rule.Description,
            !string.IsNullOrWhiteSpace(rule.FixExpression),
            obj.Kind);

    private static string ReferenceObjectType(ModelObject obj)
    {
        if (obj.Property("ObjectType") == "Model")
            return "Model";

        return obj.Kind switch
        {
            ModelObjectKind.Table => "Table (Import)",
            ModelObjectKind.Column => "Column",
            ModelObjectKind.Measure => "Measure",
            ModelObjectKind.Partition => "Partition",
            ModelObjectKind.Relationship => "Relationship",
            _ => obj.Kind.ToString()
        };
    }

    private static string ReferenceObjectName(ModelObject obj)
    {
        if (obj.Property("ObjectType") == "Model")
            return "Model";

        var parts = obj.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return obj.Kind switch
        {
            ModelObjectKind.Table => $"'{obj.Name}'",
            ModelObjectKind.Column when parts.Length >= 2 => $"'{parts[0]}'[{obj.Name}]",
            ModelObjectKind.Measure => $"[{obj.Name}]",
            _ => obj.Name
        };
    }
}

public sealed record BpaEngineOptions(
    IReadOnlyList<BpaRule> Rules,
    string? PathFilter = null,
    IReadOnlyList<string>? RuleIds = null,
    IReadOnlyList<string>? DisabledRuleIds = null);
