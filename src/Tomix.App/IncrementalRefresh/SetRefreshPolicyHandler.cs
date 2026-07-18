using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.IncrementalRefresh;

public sealed class SetRefreshPolicyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public SetRefreshPolicyHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    // M2 transitional: removed once the CLI threads stores from the composition root.
    public SetRefreshPolicyHandler(IEnumerable<IModelProvider> providers)
        : this(providers, MutationStores.Ambient())
    {
    }

    public async Task<TomixResult<SetRefreshPolicyResult>> HandleAsync(
        SetRefreshPolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Revert && !request.HasPolicyOptions)
            return TomixResult<SetRefreshPolicyResult>.Fail(
                "TOMIX_REFRESH_POLICY_NO_OPTIONS",
                "No policy options provided. Pass at least one option, e.g. --rolling-window-periods.",
                exitCode: 2,
                hint: "Use 'tx incremental-refresh show' to view the current policy.");

        // Options alongside --revert would be silently discarded with the staged copy; reject
        // them so the user's intended change is never dropped without notice.
        if (request.Revert && request.HasPolicyOptions)
            return TomixResult<SetRefreshPolicyResult>.Fail(
                "TOMIX_STAGE_OPTIONS_CONFLICT",
                "--revert discards the staged mutation; it cannot be combined with policy options.",
                exitCode: 2);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "incremental-refresh", _stores,
            (mutator, _, _) =>
            {
                var mutation = MutationCapabilities.RequireRefreshPolicies(mutator).SetRefreshPolicy(new RefreshPolicySetRequest(
                    request.Table,
                    request.Mode,
                    request.RollingWindowGranularity,
                    request.RollingWindowPeriods,
                    request.IncrementalGranularity,
                    request.IncrementalPeriods,
                    request.IncrementalOffset,
                    request.PollingExpression,
                    request.SourceExpression,
                    request.Force));

                var issues = mutation.Policy.Issues;
                return Task.FromResult<(bool, string, Func<MutationOutcome, SetRefreshPolicyResult>)>((
                    true,
                    $"incremental-refresh set {mutation.Policy.Table}",
                    outcome => new SetRefreshPolicyResult(
                        mutation.Policy.Table,
                        mutation.Created,
                        mutation.Policy,
                        outcome.Saved,
                        CreatedExpressions: mutation.CreatedExpressions.Count > 0 ? mutation.CreatedExpressions : null,
                        Issues: issues.Count > 0 ? issues : null,
                        Staged: outcome.Staged == true ? true : null,
                        Synced: outcome.Synced,
                        SyncTarget: outcome.SyncTarget,
                        SyncWarning: outcome.SyncWarning)));
            },
            new SetRefreshPolicyResult(request.Table, Created: false, Policy: null, Saved: false, Reverted: true),
            cancellationToken);
    }
}
