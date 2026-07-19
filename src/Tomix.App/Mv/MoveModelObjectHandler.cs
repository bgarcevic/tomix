using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Results;

namespace Tomix.App.Mv;

public sealed class MoveModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public MoveModelObjectHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<MoveModelObjectResult>> HandleAsync(
        MoveModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var plan = RenamePlan.Create(request.Source, request.Destination);
        if (!request.Revert && plan.Error is { } error)
            return TomixResult<MoveModelObjectResult>.Fail(error.Code, error.Message, error.ExitCode, error.Hint);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "mv", _stores,
            async (mutator, session, _) =>
            {
                // A rename or move alone doesn't rewrite DAX that references the old name. Plan
                // the rewrites while the model is intact; by default apply them (before the
                // mutation, so every path in the plan still resolves), otherwise warn — or fail
                // under --strict-refs. Case-only renames break nothing and plan empty; a move
                // breaks only fully-qualified references, which the plan filters to.
                var fixup = plan.NewParent is null && plan.CaseOnly
                    ? RenameFixupPlan.Empty
                    : await RenameFixup.PlanAsync(
                        session, request.Source, request.Type, plan.NewName, plan.NewParent, cancellationToken);
                var broken = RenameReferences.Apply(mutator, fixup, request.FixRefs, request.StrictRefs);

                if (plan.NewParent is { } newParent)
                    MutationCapabilities.RequireObjectMove(mutator).MoveObject(new ModelObjectMoveRequest(
                        request.Source,
                        request.Type,
                        newParent,
                        plan.NewName));
                else
                    mutator.SetProperty(new ModelObjectSetRequest(
                        request.Source,
                        [new ModelPropertyAssignment("name", plan.NewName)],
                        request.Type));

                return (true, $"mv {request.Source} -> {request.Destination}",
                    outcome => new MoveModelObjectResult(
                        plan.SourceDisplay, plan.DestinationDisplay,
                        outcome.Saved, outcome.Staged,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning,
                        BrokenReferences: broken.Count > 0 ? broken : null,
                        FixedReferences: request.FixRefs && fixup.FixedPaths.Count > 0 ? fixup.FixedPaths : null));
            },
            new MoveModelObjectResult(plan.SourceDisplay, plan.DestinationDisplay, false, null, Reverted: true),
            cancellationToken);
    }
}

internal sealed record RenamePlanError(string Code, string Message, int ExitCode, string? Hint = null);

/// <summary>
/// The rename — or, when <paramref name="NewParent"/> is set, the cross-table move — derived
/// from mv's source/destination arguments. Both paths go through the same quote- and DAX-aware
/// parsing the mutation resolver uses, so a leaf name keeps its apostrophes and a
/// <c>'Table'[Child]</c> destination yields <c>Child</c> — not the whole bracket string.
/// </summary>
internal sealed record RenamePlan(
    string NewName,
    bool CaseOnly,
    string SourceDisplay,
    string DestinationDisplay,
    RenamePlanError? Error,
    string? NewParent = null)
{
    public static RenamePlan Create(string source, string destination)
    {
        var sourceParts = Parts(source);
        var destinationParts = Parts(destination);
        var sourceDisplay = sourceParts.Count == 0 ? source.Trim() : string.Join('/', sourceParts);
        var destinationDisplay = destinationParts.Count == 0 ? destination.Trim() : string.Join('/', destinationParts);

        RenamePlan Fail(string code, string message, int exitCode, string? hint = null)
            => new("", false, sourceDisplay, destinationDisplay, new RenamePlanError(code, message, exitCode, hint));

        if (sourceParts.Count == 0 || sourceParts[^1].Length == 0)
            return Fail("TOMIX_MOVE_INVALID_PATH", "Source path is missing an object name.", 2);
        if (destinationParts.Count == 0 || destinationParts[^1].Length == 0)
            return Fail("TOMIX_MOVE_INVALID_PATH", "Destination path is missing an object name.", 2);

        if (!sourceParts.SkipLast(1).SequenceEqual(destinationParts.SkipLast(1), StringComparer.OrdinalIgnoreCase))
        {
            // Parents differ: a cross-table move. Only measures can move (the provider
            // enforces that); the plan just needs a 'Table/Name' destination to move under.
            if (destinationParts.Count != 2 || sourceParts.Count < 2)
                return Fail(
                    "TOMIX_MOVE_UNSUPPORTED",
                    "A cross-table move needs a 'Table/Measure' source and destination.",
                    1,
                    "Only measures can move between tables, e.g. 'Sales/Revenue' 'Metrics/Revenue'.");

            return new RenamePlan(
                destinationParts[^1],
                CaseOnly: false,
                sourceDisplay,
                destinationDisplay,
                Error: null,
                NewParent: destinationParts[0]);
        }

        var newName = destinationParts[^1];
        var oldName = sourceParts[^1];
        if (string.Equals(newName, oldName, StringComparison.Ordinal))
            return Fail(
                "TOMIX_MOVE_NOOP",
                $"Source and destination are the same ('{destinationDisplay}'); nothing to rename.",
                1);

        return new RenamePlan(
            newName,
            CaseOnly: string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase),
            sourceDisplay,
            destinationDisplay,
            Error: null);
    }

    private static List<string> Parts(string path)
    {
        if (DaxObjectForm.TryParse(path, out var table, out var child))
            return [table, child];

        var trimmed = path.Trim();
        var parts = ObjectPath.Parse(trimmed.Trim('/'))
            .Select(s => s.IsQuoted ? s.Text : s.Text.Trim())
            .ToList();

        // ObjectPath drops a trailing empty segment, but 'Sales/' means "no object name",
        // not "rename to Sales" — keep the emptiness visible for validation.
        if (trimmed.EndsWith('/') && parts.Count > 0)
            parts.Add("");

        return parts;
    }
}
