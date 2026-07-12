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
            async (mutator, _, _) =>
            {
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
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
            },
            new RemoveModelObjectResult(false, null, null, null, null, Reverted: true),
            cancellationToken);
    }
}
