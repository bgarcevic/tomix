using System.Text.Json.Nodes;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

/// <summary>Which rule edit <see cref="BpaRulesModelHandler"/> makes.</summary>
public enum BpaRulesModelAction
{
    Add,
    Set,
    Remove
}

public sealed record BpaRulesModelRequest(
    ModelReference Model,
    BpaRulesModelAction Action,
    string RuleId,
    BpaRuleFields? Fields = null,
    bool Save = false,
    string? SaveTo = null,
    string Serialization = "",
    bool Overwrite = false,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false,
    bool Force = false);

/// <summary>
/// The result of <c>bpa rules add/set/remove</c> against a model. The rule fields match
/// <see cref="BpaRulesFileResult"/>; the persistence fields come from <see cref="MutationResult"/>.
/// </summary>
public sealed record BpaRulesModelResult(
    string Action,
    string ModelName,
    bool Changed,
    int RuleCount,
    string RuleId,
    BpaRuleInfo? Rule = null,
    IReadOnlyList<string>? ChangedFields = null) : MutationResult;

/// <summary>
/// Adds, changes, or deletes a rule in the model's <c>BestPracticeAnalyzer</c> annotation — the
/// rules that ship with the model. Field checks and JSON editing are shared with rules files
/// (<see cref="BpaRulesFile"/>), so unknown fields in the annotation are kept. The edit goes
/// through <see cref="MutationRunner"/>, so <c>--save</c>, <c>--stage</c>, and the other lifecycle
/// options behave as they do for <c>bpa rules ignore</c>. A rule found only under the historical
/// misspelled key is moved to the correct one on the first edit.
/// </summary>
public sealed class BpaRulesModelHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly MutationStores _stores;

    public BpaRulesModelHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
        : this(new OneShotSessionSource(providers), stores)
    {
    }

    public BpaRulesModelHandler(IModelSessionSource sessions, MutationStores stores)
    {
        _sessions = sessions;
        _stores = stores;
    }

    public async Task<TomixResult<BpaRulesModelResult>> HandleAsync(
        BpaRulesModelRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesModelResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        var fields = request.Fields ?? new BpaRuleFields();
        if (request.Action == BpaRulesModelAction.Set && fields.IsEmpty)
            return TomixResult<BpaRulesModelResult>.Fail(
                "TOMIX_BPA_RULE_FIELD_REQUIRED",
                "Nothing to change.",
                exitCode: 2,
                hint: "Pass at least one of --name, --category, --severity, --scope, --expression, --description, --fix-expression.");

        var action = request.Action.ToString().ToLowerInvariant();
        var ruleId = request.RuleId.Trim();
        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert,
            request.Serialization, request.Force, Overwrite: request.Overwrite, NoSync: request.NoSync);

        // Set inside the mutation (where the model is already open) and returned after it: the
        // mutation reports "unchanged", so nothing is saved or staged.
        TomixResult<BpaRulesFileResult>? failure = null;

        var result = await MutationRunner.RunAsync(
            _sessions, request.Model, options, $"bpa-rules-{action}", _stores,
            async (mutator, session, _) =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);
                var properties = snapshot.Properties;
                var current = Annotation(properties, BpaModelRuleLoader.EmbeddedKey);
                var legacy = Annotation(properties, BpaModelRuleLoader.EmbeddedLegacyKey);

                BpaRulesModelResult Build(MutationOutcome outcome, bool changed, int count,
                    BpaRuleInfo? rule = null, IReadOnlyList<string>? changedFields = null)
                    => new(action, snapshot.Name, changed, count, ruleId, rule, changedFields) { Outcome = outcome };

                (bool, string, Func<MutationOutcome, BpaRulesModelResult>) Fail(TomixResult<BpaRulesFileResult> error)
                {
                    failure = error;
                    return (false, "", outcome => Build(outcome, false, 0));
                }

                // The loader reads the misspelled key only when the correct one is absent; edit the same rules.
                if (!BpaRulesFile.TryFromAnnotation(current ?? legacy, out var rules, out var reason))
                    return Fail(BpaRulesFile.LoadFailed($"the model's {BpaModelRuleLoader.EmbeddedKey} annotation", reason));

                JsonObject? rule;
                IReadOnlyList<string>? changedFields = null;
                switch (request.Action)
                {
                    case BpaRulesModelAction.Add:
                        if (rules.TryAdd(ruleId, fields, out var added, " <model>") is { } invalid)
                            return Fail(invalid);
                        rule = added;
                        break;

                    case BpaRulesModelAction.Set:
                        if (rules.Find(ruleId) is not { } existing)
                            return Fail(BpaRulesRemoveHandler.NotInFile(ruleId, rules.Path));
                        if (BpaRulesFile.TryApply(existing, fields, out var changed) is { } rejected)
                            return Fail(rejected);
                        rule = existing;
                        changedFields = changed;
                        break;

                    default:
                        if (rules.Find(ruleId) is not { } removed)
                            return Fail(BpaRulesRemoveHandler.NotInFile(ruleId, rules.Path));
                        rules.Remove(removed);
                        rule = null;
                        break;
                }

                var info = rule is null ? null : rules.Describe(rule);
                var migrate = legacy is not null && current is null;
                if (changedFields is { Count: 0 } && !migrate)
                    return (false, "", outcome => Build(outcome, false, rules.Count, info, changedFields));

                // An annotation with no rules left is removed rather than kept as "[]".
                var assignments = new List<ModelPropertyAssignment>
                {
                    new($"Annotation:{BpaModelRuleLoader.EmbeddedKey}", rules.Count == 0 ? "" : rules.ToAnnotationJson())
                };
                if (legacy is not null)
                    assignments.Add(new($"Annotation:{BpaModelRuleLoader.EmbeddedLegacyKey}", ""));

                mutator.SetProperty(new ModelObjectSetRequest(".", assignments, Type: null));

                var changedAny = changedFields is not { Count: 0 };
                return (true, $"bpa-rules-{action} {ruleId}",
                    outcome => Build(outcome, changedAny, rules.Count, info, changedFields));
            },
            outcome => new BpaRulesModelResult(action, "", false, 0, ruleId) { Outcome = outcome },
            cancellationToken);

        return failure is null
            ? result
            : new TomixResult<BpaRulesModelResult>(false, default, failure.Diagnostics, failure.ExitCode);
    }

    private static string? Annotation(IReadOnlyDictionary<string, string>? properties, string name)
        => properties is not null && properties.TryGetValue($"Annotation:{name}", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
