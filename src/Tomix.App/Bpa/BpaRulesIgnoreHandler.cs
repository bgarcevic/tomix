using System.Text.Json.Serialization;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesIgnoreRequest(
    ModelReference Model,
    string RuleId,
    bool Ignore,
    bool Save = false,
    string? SaveTo = null,
    string Serialization = "",
    bool Force = false,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false);

public sealed record BpaRulesIgnoreResult(
    string RuleId,
    bool Ignored,
    bool Changed,
    IReadOnlyList<string> RuleIds,
    object Saved,
    bool? Staged,
    string ModelName,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null);

public sealed class BpaRulesIgnoreHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public BpaRulesIgnoreHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    // M2 transitional: removed once the CLI threads stores from the composition root.
    public BpaRulesIgnoreHandler(IEnumerable<IModelProvider> providers)
        : this(providers, MutationStores.Ambient())
    {
    }

    public async Task<TomixResult<BpaRulesIgnoreResult>> HandleAsync(
        BpaRulesIgnoreRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesIgnoreResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert,
            request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "bpa-ignore", _stores,
            async (mutator, session, _) =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);

                var current = new HashSet<string>(
                    BpaIgnoreStore.ReadRuleIds(snapshot.Properties), StringComparer.OrdinalIgnoreCase);
                var hadLegacyKey = BpaIgnoreStore.HasLegacyKey(snapshot.Properties);

                var setChanged = request.Ignore ? current.Add(request.RuleId) : current.Remove(request.RuleId);
                var changed = setChanged || hadLegacyKey;

                if (!changed)
                    return (false, "", _ => new BpaRulesIgnoreResult(
                        request.RuleId, request.Ignore, false,
                        current.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        false, null, snapshot.Name));

                mutator.SetProperty(new ModelObjectSetRequest(
                    ".",
                    [
                        new ModelPropertyAssignment($"Annotation:{BpaIgnoreStore.Key}", BpaIgnoreStore.Serialize(current)),
                        new ModelPropertyAssignment($"Annotation:{BpaIgnoreStore.LegacyKey}", "")
                    ],
                    Type: null));

                return (true, $"bpa-ignore {request.RuleId}",
                    outcome => new BpaRulesIgnoreResult(
                        request.RuleId, request.Ignore, true,
                        current.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        outcome.Saved, outcome.Staged, snapshot.Name,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
            },
            new BpaRulesIgnoreResult("", false, false, [], false, null, ""),
            cancellationToken);
    }
}
