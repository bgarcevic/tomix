using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.IncrementalRefresh;

public sealed class RemoveRefreshPolicyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public RemoveRefreshPolicyHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<RemoveRefreshPolicyResult>> HandleAsync(
        RemoveRefreshPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "incremental-refresh", _stores,
            (mutator, _, _) =>
            {
                var policies = MutationCapabilities.RequireRefreshPolicies(mutator);

                // Capture the policy-generated partitions before removal: they stay on the
                // table and hold real data, so the CLI warns about them.
                var remaining = request.Revert
                    ? null
                    : policies.GetRefreshPolicy(request.Table)?.PolicyPartitions;

                var mutation = policies.RemoveRefreshPolicy(request.Table, request.IfExists);

                return Task.FromResult<(bool, string, Func<MutationOutcome, RemoveRefreshPolicyResult>)>((
                    mutation.Changed,
                    $"incremental-refresh rm {mutation.Path}",
                    outcome => new RemoveRefreshPolicyResult(
                        mutation.Changed ? mutation.Path : (object)false,
                        outcome.Saved,
                        outcome.Staged,
                        mutation.Changed ? null : mutation.Reason,
                        RemainingPolicyPartitions: mutation.Changed && remaining is { Count: > 0 } ? remaining : null,
                        Synced: outcome.Synced,
                        SyncTarget: outcome.SyncTarget,
                        SyncWarning: outcome.SyncWarning)));
            },
            new RemoveRefreshPolicyResult(false, null, null, null, Reverted: true),
            cancellationToken);
    }
}
