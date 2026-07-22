using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Mv;

/// <summary>
/// Thrown when mv resolves to no actual change (same table, display folder, and name).
/// Mapped to <c>TOMIX_MOVE_NOOP</c> by <see cref="MutationRunner"/>.
/// </summary>
public sealed class MoveNoopException : Exception
{
    public MoveNoopException(string message)
        : base(message)
    {
    }
}

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
        var plan = MovePathPlan.Create(request.Source, request.Destination);
        if (!request.Revert && plan.Error is { } error)
            return TomixResult<MoveModelObjectResult>.Fail(error.Code, error.Message, error.ExitCode, error.Hint);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "mv", _stores,
            async (mutator, session, _) =>
            {
                // What the destination means depends on what the source IS: middle segments are
                // a hierarchy for a level but display folders for a measure/column/hierarchy.
                // Resolve the source against the snapshot first, then interpret.
                var step = await ClassifyAsync(session, plan, request, cancellationToken);

                // A rename or move alone doesn't rewrite DAX that references the old name. Plan
                // the rewrites while the model is intact; by default apply them (before the
                // mutation, so every path in the plan still resolves), otherwise warn — or fail
                // under --strict-refs. Display-folder changes and case-only renames break no
                // DAX and plan nothing; a move breaks only fully-qualified references, which
                // the plan filters to.
                var fixup = step.NeedsFixup
                    ? await RenameFixup.PlanAsync(
                        session, step.FixupPath, step.FixupType, step.NewName, step.NewTable, cancellationToken)
                    : RenameFixupPlan.Empty;
                var broken = RenameReferences.Apply(mutator, fixup, request.FixRefs, request.StrictRefs);

                if (step.NewTable is { } newTable)
                    MutationCapabilities.RequireObjectMove(mutator).MoveObject(new ModelObjectMoveRequest(
                        step.MutationPath,
                        step.Type,
                        newTable,
                        step.NewName,
                        step.NewFolder));
                else
                    mutator.SetProperty(new ModelObjectSetRequest(
                        step.MutationPath,
                        step.Assignments,
                        step.Type));

                return (true, $"mv {plan.SourceDisplay} -> {step.DestinationDisplay}",
                    outcome => new MoveModelObjectResult(
                        plan.SourceDisplay, step.DestinationDisplay,
                        outcome.Saved, outcome.Staged,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning,
                        BrokenReferences: broken.Count > 0 ? broken : null,
                        FixedReferences: request.FixRefs && fixup.FixedPaths.Count > 0 ? fixup.FixedPaths : null));
            },
            new MoveModelObjectResult(plan.SourceDisplay, plan.DestinationDisplay, false, null, Reverted: true),
            cancellationToken);
    }

    private static bool SupportsFolders(ModelObjectKind kind)
        => kind is ModelObjectKind.Measure or ModelObjectKind.Column or ModelObjectKind.Hierarchy;

    private static string CurrentFolder(ModelObject obj)
        => obj.Property(PropertyBagKeys.DisplayFolder) ?? "";

    private static async Task<MoveStep> ClassifyAsync(
        IModelSession session,
        MovePathPlan plan,
        MoveModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Exact lookup first: it matches everything the path grammar names directly, including
        // levels at Table/Hierarchy/Level — those take precedence over a folder reading.
        var matches = ModelObjectLookup.Find(snapshot, request.Source, request.Type);
        var sourceFolderQualified = false;

        if (matches.Count == 0 && plan.SourceParts.Count > 2)
        {
            // Folder-qualified source (Table/Folder…/Leaf): match by table + leaf, then require
            // the object to actually sit in the named folder so typos don't move the wrong thing.
            var expected = string.Join('\\', plan.SourceParts.Skip(1).SkipLast(1));
            var candidates = ModelObjectLookup
                .Find(snapshot, $"{plan.SourceParts[0]}/{plan.SourceParts[^1]}", request.Type)
                .Where(m => SupportsFolders(m.Kind))
                .ToList();
            matches = candidates
                .Where(m => string.Equals(CurrentFolder(m), expected, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count > 0 && matches.Count == 0)
            {
                var actual = CurrentFolder(candidates[0]);
                throw new ObjectNotFoundException(
                    $"Object '{plan.SourceDisplay}' not found.",
                    hint: actual.Length > 0
                        ? $"'{candidates[0].Path}' is in display folder '{actual}'."
                        : $"'{candidates[0].Path}' has no display folder.");
            }

            sourceFolderQualified = true;
        }

        if (matches.Count > 1)
            throw new AmbiguousObjectException(
                $"{AmbiguousMatchMessage.For(request.Source, matches)} {AmbiguousMatchMessage.Hint}");

        return matches.Count == 1
            ? ClassifyResolved(matches[0], plan, request, sourceFolderQualified)
            : ClassifyUnresolved(plan, request);
    }

    private static MoveStep ClassifyResolved(
        ModelObject match,
        MovePathPlan plan,
        MoveModelObjectRequest request,
        bool sourceFolderQualified)
    {
        var matchParts = MovePathPlan.PathParts(match.Path);
        var dst = plan.DestinationParts;
        var type = request.Type ?? match.Kind;

        // The typed source may be a bare name, keyword form, or folder-qualified; the resolved
        // snapshot path is the one shape the mutation resolver is guaranteed to accept.
        var mutationPath = sourceFolderQualified || plan.SourceParts.Count != matchParts.Count
            ? match.Path
            : request.Source;

        if (SupportsFolders(match.Kind))
        {
            string destTable;
            string leaf;
            IReadOnlyList<string> folders;
            if (dst.Count == 1 && !plan.KeepName)
            {
                // A lone leaf renames in place, keeping table and folder.
                destTable = matchParts[0];
                leaf = dst[0];
                folders = [];
            }
            else
            {
                destTable = dst[0];
                leaf = plan.KeepName ? match.Name : dst[^1];
                folders = plan.KeepName
                    ? dst.Skip(1).ToList()
                    : dst.Skip(1).SkipLast(1).ToList();
            }

            var newFolder = string.Join('\\', folders);
            // Folder segments in either path make the destination folder explicit; a plain
            // rename ('Sales/Old' 'Sales/New') never touches the folder the object is in.
            var folderIntent = folders.Count > 0 || sourceFolderQualified;
            var currentFolder = CurrentFolder(match);
            var destDisplay = string.Join('/', new[] { destTable }.Concat(folders).Append(leaf));

            if (string.Equals(destTable, matchParts[0], StringComparison.OrdinalIgnoreCase))
            {
                var nameChanged = !string.Equals(leaf, match.Name, StringComparison.Ordinal);
                var folderChanged = folderIntent && !string.Equals(newFolder, currentFolder, StringComparison.Ordinal);
                if (!nameChanged && !folderChanged)
                    throw new MoveNoopException(
                        $"Nothing to move: '{plan.SourceDisplay}' is already at '{destDisplay}'.");

                var assignments = new List<ModelPropertyAssignment>();
                if (folderChanged)
                    assignments.Add(new ModelPropertyAssignment("displayFolder", newFolder));
                if (nameChanged)
                    assignments.Add(new ModelPropertyAssignment("name", leaf));

                return new MoveStep(
                    mutationPath, type, leaf,
                    NewTable: null, NewFolder: null,
                    assignments,
                    NeedsFixup: nameChanged && !string.Equals(leaf, match.Name, StringComparison.OrdinalIgnoreCase),
                    FixupPath: match.Path, FixupType: match.Kind,
                    destDisplay);
            }

            // Cross-table: the provider enforces that only measures can move. Without folder
            // segments the moved object keeps the folder it had.
            return new MoveStep(
                mutationPath, type, leaf,
                NewTable: destTable,
                NewFolder: folders.Count > 0 ? newFolder : sourceFolderQualified ? "" : null,
                Assignments: [],
                NeedsFixup: true,
                FixupPath: match.Path, FixupType: match.Kind,
                destDisplay);
        }

        // Everything else renames in place under its current parent.
        var srcParents = matchParts.Take(matchParts.Count - 1).ToList();
        var newName = plan.KeepName ? match.Name : dst[^1];
        IReadOnlyList<string> destParents = plan.KeepName ? dst : dst.Take(dst.Count - 1).ToList();
        if (!destParents.SequenceEqual(srcParents, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(
                match.Kind == ModelObjectKind.Level
                    ? "Levels can only be renamed within their hierarchy, e.g. 'Sales/Calendar/Year' 'Sales/Calendar/CalendarYear'."
                    : $"A {ModelObjectProjection.KindLabel(match.Kind)} can only be renamed in place; "
                      + "only measures move between tables, and only measures, columns, and hierarchies have display folders.");

        if (string.Equals(newName, match.Name, StringComparison.Ordinal))
            throw new MoveNoopException(
                $"Nothing to move: '{plan.SourceDisplay}' is already at '{plan.DestinationDisplay}'.");

        return new MoveStep(
            mutationPath, type, newName,
            NewTable: null, NewFolder: null,
            Assignments: [new ModelPropertyAssignment("name", newName)],
            NeedsFixup: !string.Equals(newName, match.Name, StringComparison.OrdinalIgnoreCase),
            FixupPath: match.Path, FixupType: match.Kind,
            DestinationDisplay: string.Join('/', destParents.Append(newName)));
    }

    /// <summary>
    /// The source names something outside the snapshot (shared expressions and functions have
    /// no snapshot kind). Fall back to the string-shape rules and let the mutation resolver be
    /// the authority — including its not-found error when the object truly doesn't exist.
    /// </summary>
    private static MoveStep ClassifyUnresolved(MovePathPlan plan, MoveModelObjectRequest request)
    {
        var src = plan.SourceParts;
        var dst = plan.DestinationParts;
        var newName = plan.KeepName ? src[^1] : dst[^1];
        IReadOnlyList<string> destParents = plan.KeepName ? dst : dst.Take(dst.Count - 1).ToList();

        if (destParents.SequenceEqual(src.Take(src.Count - 1), StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(newName, src[^1], StringComparison.Ordinal))
                throw new MoveNoopException(
                    $"Nothing to move: '{plan.SourceDisplay}' is already at '{plan.DestinationDisplay}'.");

            return new MoveStep(
                request.Source, request.Type, newName,
                NewTable: null, NewFolder: null,
                Assignments: [new ModelPropertyAssignment("name", newName)],
                NeedsFixup: !string.Equals(newName, src[^1], StringComparison.OrdinalIgnoreCase),
                FixupPath: request.Source, FixupType: request.Type,
                DestinationDisplay: string.Join('/', destParents.Append(newName)));
        }

        if (dst.Count == 2 && src.Count >= 2 && !plan.KeepName)
            return new MoveStep(
                request.Source, request.Type, newName,
                NewTable: dst[0], NewFolder: null,
                Assignments: [],
                NeedsFixup: true,
                FixupPath: request.Source, FixupType: request.Type,
                DestinationDisplay: plan.DestinationDisplay);

        throw new NotSupportedException(
            "A cross-table move needs a 'Table/Measure' source and destination. "
            + "Only measures can move between tables, e.g. 'Sales/Revenue' 'Metrics/Revenue'.");
    }

    /// <summary>One fully classified mv: either a property write (rename and/or display-folder
    /// change) or a cross-table move, plus what the DAX fixup needs to plan it.</summary>
    private sealed record MoveStep(
        string MutationPath,
        ModelObjectKind? Type,
        string NewName,
        string? NewTable,
        string? NewFolder,
        IReadOnlyList<ModelPropertyAssignment> Assignments,
        bool NeedsFixup,
        string FixupPath,
        ModelObjectKind? FixupType,
        string DestinationDisplay);
}

internal sealed record RenamePlanError(string Code, string Message, int ExitCode, string? Hint = null);

/// <summary>
/// The string-level shape of mv's source/destination arguments: keyword-stripped path segments,
/// whether the destination ends in <c>/</c> (keep the source name), and the syntax errors that
/// need no model to detect. Both paths go through the same quote- and DAX-aware parsing the
/// mutation resolver uses, so a leaf name keeps its apostrophes and a <c>'Table'[Child]</c>
/// destination yields <c>Child</c> — not the whole bracket string. What the middle segments
/// MEAN (hierarchy vs display folder) is decided later against the resolved source object.
/// </summary>
internal sealed record MovePathPlan(
    IReadOnlyList<string> SourceParts,
    IReadOnlyList<string> DestinationParts,
    bool KeepName,
    string SourceDisplay,
    string DestinationDisplay,
    RenamePlanError? Error)
{
    public static MovePathPlan Create(string source, string destination)
    {
        var (sourceParts, sourceTrailing) = Parse(source);
        var (destinationParts, keepName) = Parse(destination);
        var sourceDisplay = sourceParts.Count == 0 ? source.Trim() : string.Join('/', sourceParts);
        var destinationDisplay = destinationParts.Count == 0 ? destination.Trim() : string.Join('/', destinationParts);

        MovePathPlan Fail(string code, string message, int exitCode, string? hint = null)
            => new(sourceParts, destinationParts, keepName, sourceDisplay, destinationDisplay,
                new RenamePlanError(code, message, exitCode, hint));

        if (sourceParts.Count == 0 || sourceTrailing)
            return Fail("TOMIX_MOVE_INVALID_PATH", "Source path is missing an object name.", 2);
        if (destinationParts.Count == 0)
            return Fail("TOMIX_MOVE_INVALID_PATH", "Destination path is missing an object name.", 2);

        if (destinationParts.Count == 1 && !keepName && sourceParts.Count > 1)
        {
            // 'Sales/Revenue' -> 'Metrics' is ambiguous between a rename and a table move;
            // require the full destination path (or a trailing slash to keep the name).
            if (!string.Equals(destinationParts[0], sourceParts[^1], StringComparison.Ordinal))
                return Fail(
                    "TOMIX_MOVE_UNSUPPORTED",
                    "A cross-table move needs a 'Table/Measure' source and destination.",
                    1,
                    "Only measures can move between tables, e.g. 'Sales/Revenue' 'Metrics/Revenue'.");
        }

        if (!keepName
            && sourceParts.Count == destinationParts.Count
            && string.Equals(sourceParts[^1], destinationParts[^1], StringComparison.Ordinal)
            && sourceParts.SkipLast(1).SequenceEqual(destinationParts.SkipLast(1), StringComparer.Ordinal))
            return Fail(
                "TOMIX_MOVE_NOOP",
                $"Source and destination are the same ('{destinationDisplay}'); nothing to rename.",
                1);

        return new MovePathPlan(sourceParts, destinationParts, keepName, sourceDisplay, destinationDisplay, Error: null);
    }

    /// <summary>Keyword-stripped segments of a snapshot object path.</summary>
    public static List<string> PathParts(string path)
        => Parse(path).Parts;

    private static (List<string> Parts, bool TrailingSlash) Parse(string path)
    {
        if (DaxObjectForm.TryParse(path, out var table, out var child))
            return ([table, child], false);

        var trimmed = path.Trim();
        var trailingSlash = trimmed.Length > 1 && trimmed.EndsWith('/');
        var parts = new List<string>();
        foreach (var segment in ObjectPath.Parse(trimmed.Trim('/')))
        {
            // Container keywords (tables/, measures/, …) address, they don't name — drop them
            // so 'tables/Sales/measures/X' and 'Sales/X' compare equal.
            if (segment.TryGetKeyword(out _))
                continue;

            var text = segment.IsQuoted ? segment.Text : segment.Text.Trim();
            if (text.Length > 0)
                parts.Add(text);
        }

        return (parts, trailingSlash);
    }
}
