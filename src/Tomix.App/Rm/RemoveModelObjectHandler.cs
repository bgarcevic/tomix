using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Rm;

public sealed class RemoveModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public RemoveModelObjectHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

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
            request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "rm",
            async (mutator, session, _) =>
            {
                // A removal cannot be fixed up like a rename — the referenced object is gone.
                // DAX still referencing it blocks the removal; --force removes anyway and
                // reports the referencing objects as broken.
                var referencing = await RemoveGuard.ReferencingPathsAsync(
                    session, request.Path, request.Type, cancellationToken);
                if (referencing.Count > 0 && !request.Force)
                    throw new RemoveBrokenReferencesException(RemoveGuard.BlockedMessage(referencing));

                var mutation = mutator.RemoveObject(new ModelObjectRemoveRequest(
                    request.Path,
                    request.Type,
                    request.IfExists));

                return (mutation.Changed, $"rm {mutation.Path}",
                    outcome => new RemoveModelObjectResult(
                        mutation.Changed ? mutation.Path : (object)false,
                        outcome.Saved, outcome.Staged,
                        mutation.Changed ? null : mutation.Reason,
                        mutation.Changed ? null : mutation.Path,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning,
                        BrokenReferences: mutation.Changed && referencing.Count > 0 ? referencing : null,
                        CascadeRemoved: mutation.CascadeRemoved));
            },
            new RemoveModelObjectResult(false, null, null, null, null, Reverted: true),
            cancellationToken);
    }
}
