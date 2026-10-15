using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Rm;

/// <summary>
/// Removes several objects of any kinds as one mutation: one validation, one save, and in a live
/// session one journal entry (one undo step). Every target is resolved, and the guards evaluated
/// against the whole deletion set, before anything changes:
/// <list type="bullet">
/// <item>DAX in a surviving object that references a target blocks unless <c>--force</c>;
/// references between targets never do, whatever the input order.</item>
/// <item>A surviving partition bound to a data-source target blocks, <c>--force</c> or not.</item>
/// <item>Structural cascades onto surviving objects (relationships, sort-by, hierarchy levels,
/// perspectives, role permissions) block unless <c>--cascade</c>.</item>
/// </list>
/// A preview reports what would block instead of failing.
/// </summary>
public sealed class RemoveBatchHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly MutationStores _stores;

    public RemoveBatchHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
        : this(new OneShotSessionSource(providers), stores)
    {
    }

    public RemoveBatchHandler(IModelSessionSource sessions, MutationStores stores)
    {
        _sessions = sessions;
        _stores = stores;
    }

    public async Task<TomixResult<RemoveBatchResult>> HandleAsync(
        RemoveBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Targets.Count == 0 && !request.Revert)
            return TomixResult<RemoveBatchResult>.Fail(
                "TOMIX_RM_INPUT_INVALID", "No objects to remove.",
                hint: "Pass object paths as arguments or list them with --from <file|->.", exitCode: 2);

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
            _sessions, request.Model, options, "rm", _stores,
            async (mutator, session, context) =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);
                var plan = RemoveBatchPlan.Resolve(snapshot, request.Targets, request.IfExists);
                var broken = plan.BrokenReferences();
                var bound = plan.BoundPartitions();

                if (bound.Count > 0 && context.KeepsEdit)
                    throw new InvalidOperationException(
                        string.Join(" ", bound.Select(b =>
                            $"Cannot remove data source '{b.Key.Name}'; it is used by partition(s): {string.Join(", ", b.Value)}."))
                        + " Add those partitions to the batch, or repoint them first.");
                if (broken.Count > 0 && !request.Force && context.KeepsEdit)
                    throw new RemoveBrokenReferencesException(
                        $"Removing these objects breaks {broken.Count} DAX reference(s) in: {string.Join(", ", broken)}.");

                var cascades = new List<RemoveBatchCascade>();
                var removedAny = false;
                foreach (var removal in plan.ApplyOrder)
                {
                    var target = removal.Object!;
                    if (bound.ContainsKey(target))
                        continue; // preview only: the provider refuses it, so report instead.

                    // IfExists: an earlier target's cascade may already have taken this one (a
                    // hierarchy whose levels were all targets). It was resolved above, so it is gone.
                    var mutation = mutator.RemoveObject(new ModelObjectRemoveRequest(
                        target.Path,
                        target.Kind == ModelObjectKind.CalculatedColumn ? ModelObjectKind.Column : target.Kind,
                        IfExists: true));
                    removedAny |= mutation.Changed;

                    foreach (var cascade in mutation.Cascades ?? [])
                    {
                        if (plan.IsInternal(cascade))
                            continue;
                        cascades.Add(new RemoveBatchCascade(
                            target.Path, cascade.Path, RemoveBatchPlan.KindName(cascade.Kind),
                            cascade.Description, RequiresCascade: !cascade.Owned));
                    }
                }

                var survivorCascades = cascades.Where(c => c.RequiresCascade).ToList();
                if (survivorCascades.Count > 0 && !request.Cascade && context.KeepsEdit)
                    throw new RemoveCascadeRequiredException(
                        $"Removing these objects also changes {survivorCascades.Count} object(s) outside the batch: "
                        + string.Join(", ", survivorCascades.Select(c => c.Description).Distinct()) + ".");

                var blockedBy = new List<string>();
                if (broken.Count > 0 && !request.Force)
                    blockedBy.Add(RemoveBatchResult.BlockedByReferences);
                if (survivorCascades.Count > 0 && !request.Cascade)
                    blockedBy.Add(RemoveBatchResult.BlockedByCascade);
                if (bound.Count > 0)
                    blockedBy.Add(RemoveBatchResult.BlockedByBoundPartitions);

                var targets = plan.Targets.Select(ToResult).ToList();
                var summary = $"rm {string.Join(", ", targets.Where(t => t.Action == RemoveBatchTarget.Remove).Select(t => t.Path))}";
                return (removedAny || blockedBy.Count > 0, summary,
                    outcome => new RemoveBatchResult(
                        targets,
                        cascades.Count > 0 ? cascades : null,
                        broken.Count > 0 ? broken : null,
                        bound.Count > 0 ? bound.Values.SelectMany(v => v).ToList() : null,
                        blockedBy.Count > 0 ? "would_block" : null,
                        blockedBy.Count > 0 ? blockedBy : null)
                    { Outcome = outcome });
            },
            outcome => new RemoveBatchResult([]) { Outcome = outcome },
            cancellationToken);
    }

    private static RemoveBatchTarget ToResult(PlannedRemoval removal)
    {
        if (removal.Object is not { } obj)
            return new RemoveBatchTarget(removal.Target.Label, removal.Target.Path, null, RemoveBatchTarget.NotFound);

        return new RemoveBatchTarget(
            removal.Target.Label,
            obj.Path,
            RemoveBatchPlan.KindName(obj.Kind),
            removal.SubsumedBy is null ? RemoveBatchTarget.Remove : RemoveBatchTarget.Subsumed,
            removal.SubsumedBy);
    }
}
