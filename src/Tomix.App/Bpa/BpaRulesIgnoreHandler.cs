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
    bool Overwrite = false,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false,
    bool Force = false);

public sealed record BpaRulesIgnoreResult(
    string RuleId,
    bool Ignored,
    bool Changed,
    IReadOnlyList<string> RuleIds,
    string ModelName) : MutationResult;

public sealed class BpaRulesIgnoreHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public BpaRulesIgnoreHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
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
            request.Serialization, request.Force, Overwrite: request.Overwrite, NoSync: request.NoSync);

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
                    return (false, "", outcome => new BpaRulesIgnoreResult(
                        request.RuleId, request.Ignore, false,
                        current.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        snapshot.Name)
                    { Outcome = outcome });

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
                        snapshot.Name)
                    { Outcome = outcome });
            },
            outcome => new BpaRulesIgnoreResult("", false, false, [], "") { Outcome = outcome },
            cancellationToken);
    }
}
