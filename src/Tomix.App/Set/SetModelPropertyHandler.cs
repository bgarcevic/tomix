using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Set;

public sealed class SetModelPropertyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public SetModelPropertyHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

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
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "set",
            async (mutator, session, _) =>
            {
                // Renames don't rewrite DAX that references the old name; find those references
                // while the model is still intact so the result can warn (or --strict-refs fail).
                IReadOnlyList<string> brokenRefs = [];
                if (request.Properties.Any(IsNameAssignment))
                {
                    brokenRefs = await RenameReferenceCheck.FindReferencingPathsAsync(
                        session, request.Path, request.Type, cancellationToken);
                    if (brokenRefs.Count > 0 && request.StrictRefs)
                        throw new RenameBrokenReferencesException(RenameReferenceCheck.Warning(brokenRefs));
                }

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
                        BrokenReferences: brokenRefs.Count > 0 ? brokenRefs : null));
            },
            new SetModelPropertyResult(request.Path, Property: "", Value: "", Saved: false, ValidationErrors: 0),
            cancellationToken);
    }

    private static bool IsNameAssignment(ModelPropertyAssignment assignment)
        => string.Equals(
            assignment.Property.Trim().Replace(" ", "", StringComparison.Ordinal),
            "name",
            StringComparison.OrdinalIgnoreCase);
}
