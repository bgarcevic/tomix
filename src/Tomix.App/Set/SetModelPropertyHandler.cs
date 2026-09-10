using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Set;

public sealed class SetModelPropertyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public SetModelPropertyHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<SetModelPropertyResult>> HandleAsync(
        SetModelPropertyRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Revert && request.Properties.Count == 0)
            return TomixResult<SetModelPropertyResult>.Fail(
                "TOMIX_SET_PROPERTY_REQUIRED",
                "At least one -q/-i property assignment is required.",
                exitCode: 2);

        // A -q/-i alongside --revert would be silently discarded with the staged copy; reject it
        // so the user's intended change is never dropped without notice.
        if (request.Revert && request.Properties.Count > 0)
            return TomixResult<SetModelPropertyResult>.Fail(
                "TOMIX_STAGE_OPTIONS_CONFLICT",
                "--revert discards the staged mutation; it cannot be combined with -q/-i assignments.",
                exitCode: 2);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, Force: false, request.Overwrite, request.NoSync, DryRun: request.DryRun);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "set", _stores,
            async (mutator, session, _) =>
            {
                // Capture the pre-edit value of the reported assignment for the text preview.
                // Only DAX-possible property names pay for the snapshot read; the mutator
                // decides whether the write happened. Render-only either way.
                var assignment = request.Properties[^1];
                string? oldValue = null;
                var isDaxProperty = false;
                if (DaxExpressions.MayBeDaxProperty(assignment.Property))
                {
                    var target = await FindTargetAsync(session, request.Path, request.Type, cancellationToken);
                    (oldValue, isDaxProperty) = CaptureEdit(target, assignment.Property);
                }

                // A rename alone doesn't rewrite DAX that references the old name. Plan the
                // rewrites while the model is intact; by default apply them (before the rename,
                // so every path in the plan still resolves), otherwise warn — or fail under
                // --strict-refs. The plan is empty for case-only renames.
                var fixup = RenameFixupPlan.Empty;
                if (request.Properties.LastOrDefault(IsNameAssignment) is { } rename)
                    fixup = await RenameFixup.PlanAsync(
                        session, request.Path, request.Type, rename.Value, newTable: null, cancellationToken);
                var broken = RenameReferences.Apply(mutator, fixup, request.FixRefs, request.StrictRefs);

                var mutation = mutator.SetProperty(new ModelObjectSetRequest(
                    request.Path,
                    request.Properties,
                    request.Type));

                var property = mutation.Property ?? request.Properties[^1].Property;
                return (mutation.Changed, $"set {mutation.Path}.{property}",
                    outcome => new SetModelPropertyResult(
                        mutation.Path,
                        property,
                        mutation.Value ?? request.Properties[^1].Value,
                        outcome.Saved,
                        ValidationErrors: 0,
                        outcome.Staged == true ? true : null,
                        Synced: outcome.Synced,
                        SyncTarget: outcome.SyncTarget,
                        SyncWarning: outcome.SyncWarning,
                        BrokenReferences: broken.Count > 0 ? broken : null,
                        FixedReferences: request.FixRefs && fixup.FixedPaths.Count > 0 ? fixup.FixedPaths : null,
                        DryRun: request.DryRun,
                        OldValue: oldValue,
                        IsDaxProperty: isDaxProperty));
            },
            new SetModelPropertyResult(request.Path, Property: "", Value: "", Saved: false, ValidationErrors: 0),
            cancellationToken);
    }

    private static bool IsNameAssignment(ModelPropertyAssignment assignment)
        => string.Equals(
            assignment.Property.Trim().Replace(" ", "", StringComparison.Ordinal),
            "name",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The snapshot object an assignment targets, resolved the way the mutator resolves paths
    /// (DAX bracket forms included). Null when the path does not match, in which case the
    /// mutator raises its own not-found error and there is nothing to preview.
    /// </summary>
    private static async Task<ModelObject?> FindTargetAsync(
        IModelSession session,
        string path,
        ModelObjectKind? type,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), type).FirstOrDefault();
    }

    /// <summary>
    /// The current value of the property being set and whether it carries DAX, matched against
    /// the snapshot's DAX sites case-insensitively. The main <c>Expression</c> of a DAX-bearing
    /// kind counts even when currently empty, so the first real expression still previews.
    /// Render-only data: null/false when the target is missing or the property is not DAX.
    /// </summary>
    private static (string? OldValue, bool IsDax) CaptureEdit(ModelObject? target, string property)
    {
        if (target is null)
            return (null, false);

        var key = property.Trim();
        foreach (var site in DaxExpressions.Sites(target))
        {
            if (string.Equals(site.Property, key, StringComparison.OrdinalIgnoreCase))
                return (site.Expression, true);
        }

        if (string.Equals(key, "Expression", StringComparison.OrdinalIgnoreCase)
            && DaxExpressions.IsDaxExpression(target.Kind, target.Detail))
            return (target.Expression, true);

        return (null, false);
    }
}
