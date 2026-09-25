using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Results;

namespace Tomix.App.Rm;

public sealed class RemoveModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public RemoveModelObjectHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<RemoveModelObjectResult>> HandleAsync(
        RemoveModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save && !request.DryRun,
            request.DryRun ? null : request.SaveTo,
            request.Stage && !request.DryRun,
            request.Revert,
            request.Serialization,
            request.Force,
            request.Overwrite,
            request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, RefreshPolicyPath.Table(request.Path, request.Type) is not null ? "refresh-policy" : "rm", _stores,
            async (mutator, session, _) =>
            {
                // A removal cannot be fixed up like a rename — the referenced object is gone.
                // DAX still referencing it blocks the removal; --force removes anyway and
                // reports the referencing objects as broken. A dry run reports the block
                // instead of failing: discovering the need for --force is the preview's job.
                var referencing = await RemoveGuard.ReferencingPathsAsync(
                    session, request.Path, request.Type, cancellationToken);
                if (referencing.Count > 0 && !request.Force)
                {
                    if (!request.DryRun)
                        throw new RemoveBrokenReferencesException(RemoveGuard.BlockedMessage(referencing));

                    // Guarded preview: report the would-be breakage without touching the model.
                    return (true, $"rm {request.Path}",
                        outcome => new RemoveModelObjectResult(
                            request.Path,
                            request.DryRun ? null : outcome.Saved,
                            request.DryRun ? null : outcome.Staged,
                            "would_block",
                            request.Path,
                            outcome.Synced, outcome.SyncTarget, outcome.SyncWarning,
                            BrokenReferences: referencing,
                            DryRun: true));
                }

                var mutation = mutator.RemoveObject(new ModelObjectRemoveRequest(
                    request.Path,
                    request.Type,
                    request.IfExists));

                return (mutation.Changed, $"rm {mutation.Path}",
                    outcome => new RemoveModelObjectResult(
                        mutation.Changed ? mutation.Path : (object)false,
                        request.DryRun ? null : outcome.Saved,
                        request.DryRun ? null : outcome.Staged,
                        mutation.Changed ? null : mutation.Reason,
                        mutation.Changed ? null : mutation.Path,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning,
                        BrokenReferences: mutation.Changed && referencing.Count > 0 ? referencing : null,
                        CascadeRemoved: mutation.CascadeRemoved,
                        RemainingPolicyPartitions: mutation.RemainingPolicyPartitions,
                        DryRun: request.DryRun,
                        NewValidationErrors: outcome.Validation?.NewErrorCount));
            },
            new RemoveModelObjectResult(false, null, null, null, null, Reverted: true),
            cancellationToken);
    }
}
