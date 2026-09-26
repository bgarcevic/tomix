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
    bool Force = false,
    bool AllowUnknown = false,
    string? RulesFile = null);

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
    private readonly string? _configDirectory;

    /// <param name="configDirectory">
    /// Where the user's <c>bpa-rules.json</c> lives, so its rules count as known IDs. Null checks
    /// against the bundled catalog and the model's own rules only.
    /// </param>
    public BpaRulesIgnoreHandler(IEnumerable<IModelProvider> providers, MutationStores stores, string? configDirectory = null)
    {
        _providers = providers.ToList();
        _stores = stores;
        _configDirectory = configDirectory;
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

        // Set inside the mutation (where the model is already open) and turned into a failure
        // after it: the mutation reports "unchanged", so nothing is saved or staged.
        TomixResult<BpaRulesIgnoreResult>? unknownRule = null;

        var result = await MutationRunner.RunAsync(
            _providers, request.Model, options, "bpa-ignore", _stores,
            async (mutator, session, _) =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);

                // Only ignoring is checked: unignoring must stay possible for an ID that no longer exists.
                if (request.Ignore && !request.AllowUnknown)
                {
                    var known = await BpaKnownRules.Load(_configDirectory, request.RulesFile).WithModelRulesAsync(
                        snapshot.Properties,
                        BpaModelRuleLoader.ResolveBaseDirectory(session, request.Model),
                        cancellationToken).ConfigureAwait(false);
                    unknownRule = known.Check<BpaRulesIgnoreResult>(request.RuleId);
                    if (unknownRule is not null)
                        return (false, "", outcome => new BpaRulesIgnoreResult(
                            request.RuleId, request.Ignore, false, [], snapshot.Name)
                        { Outcome = outcome });
                }

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

        return unknownRule ?? result;
    }
}
