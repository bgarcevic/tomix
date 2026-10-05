using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Results;

namespace Tomix.App.Rm;

public sealed class RemoveModelObjectHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly MutationStores _stores;

    public RemoveModelObjectHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
        : this(new OneShotSessionSource(providers), stores)
    {
    }

    public RemoveModelObjectHandler(IModelSessionSource sessions, MutationStores stores)
    {
        _sessions = sessions;
        _stores = stores;
    }

    public async Task<TomixResult<RemoveModelObjectResult>> HandleAsync(
        RemoveModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save,
            request.SaveTo,
            request.Stage,
            request.Revert,
            request.Serialization,
            request.Force,
            request.Overwrite,
            request.NoSync);

        return await MutationRunner.RunAsync(
            _sessions, request.Model, options, RefreshPolicyPath.Table(request.Path, request.Type) is not null ? "refresh-policy" : "rm", _stores,
            async (mutator, session, context) =>
            {
                // A removal cannot be fixed up like a rename — the referenced object is gone.
                // DAX still referencing it blocks the removal; --force removes anyway and
                // reports the referencing objects as broken. A preview (no --save/--stage) reports
                // the block instead of failing: discovering the need for --force is the preview's job.
                var referencing = await RemoveGuard.ReferencingPathsAsync(
                    session, request.Path, request.Type, cancellationToken);
                if (referencing.Count > 0 && !request.Force)
                {
                    if (context.KeepsEdit)
                        throw new RemoveBrokenReferencesException(RemoveGuard.BlockedMessage(referencing));

                    // Guarded preview: report the would-be breakage without touching the model.
                    return (true, $"rm {request.Path}",
                        outcome => new RemoveModelObjectResult(
                            request.Path,
                            "would_block",
                            BrokenReferences: referencing)
                        { Outcome = outcome });
                }

                var mutation = mutator.RemoveObject(new ModelObjectRemoveRequest(
                    request.Path,
                    request.Type,
                    request.IfExists));

                return (mutation.Changed, $"rm {mutation.Path}",
                    outcome => new RemoveModelObjectResult(
                        mutation.Path,
                        mutation.Changed ? null : mutation.Reason,
                        BrokenReferences: mutation.Changed && referencing.Count > 0 ? referencing : null,
                        CascadeRemoved: mutation.CascadeRemoved,
                        RemainingPolicyPartitions: mutation.RemainingPolicyPartitions)
                    { Outcome = outcome });
            },
            outcome => new RemoveModelObjectResult(null) { Outcome = outcome },
            cancellationToken);
    }
}
