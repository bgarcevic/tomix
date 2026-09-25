using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Mutations;

public enum MutationMode { None, Save, Stage, Revert }

/// <summary>The terminal-mode options shared by every mutation command (<c>--save</c>/<c>--stage</c>/<c>--revert</c>).</summary>
public sealed record MutationOptions(
    bool Save,
    string? SaveTo,
    bool Stage,
    bool Revert,
    string Serialization,
    bool Force,
    bool Overwrite = false,
    bool NoSync = false,
    bool DryRun = false);

/// <summary>Where a handler should open/mutate and how it should persist, resolved up front by <see cref="MutationLifecycle"/>.</summary>
public sealed record MutationContext(
    MutationMode Mode,
    ModelReference EffectiveModel,
    string? SaveTarget,
    string Serialization,
    bool Force,
    StagingHandle? Staging,
    ModelReference? SyncTarget = null,
    bool Overwrite = false,
    bool DryRun = false,
    bool SyncSuppressed = false);

/// <summary>A failed pre-flight: the code/message/exit-code the handler should return verbatim.</summary>
public sealed record MutationError(string Code, string Message, int ExitCode);

public sealed record MutationBegin(MutationContext? Context, MutationError? Error)
{
    public MutationMode Mode => Context?.Mode ?? MutationMode.None;
}

/// <summary>
/// The shared open/mutate/persist lifecycle for mutation handlers. It owns BOTH "which reference to
/// open" and "what to do after mutating", because <c>--stage</c> must redirect the open target to a
/// staged working copy before the provider opens anything. Generalizes <see cref="MutationSave"/>.
/// </summary>
public static class MutationLifecycle
{
    /// <summary>Resolves the terminal mode, rejecting mutually-exclusive option combinations.</summary>
    public static MutationError? ResolveMode(MutationOptions options, out MutationMode mode)
    {
        mode = MutationMode.None;
        if (options.Save && options.Stage)
            return new MutationError("TOMIX_STAGE_SAVE_CONFLICT", "--save and --stage are mutually exclusive.", 2);
        if (options.Revert && (options.Save || options.Stage || !string.IsNullOrWhiteSpace(options.SaveTo)))
            return new MutationError("TOMIX_STAGE_OPTIONS_CONFLICT", "--revert cannot be combined with --save, --save-to, or --stage.", 2);

        if (options.Revert)
            mode = MutationMode.Revert;
        else if (options.DryRun)
            mode = MutationMode.None; // --dry-run suppresses --save/--stage/--save-to
        else if (options.Stage)
            mode = MutationMode.Stage;
        else if (MutationSave.Requested(options.Save, options.SaveTo))
            mode = MutationMode.Save;

        return null;
    }

    /// <summary>
    /// Pre-flight: resolves the effective model + save target for <paramref name="source"/>. For
    /// <see cref="MutationMode.Stage"/> this materializes (or reuses) a local working copy and points
    /// the handler at it. Returns an <see cref="MutationError"/> the handler should surface on failure.
    /// </summary>
    public static async Task<MutationBegin> BeginAsync(
        IReadOnlyList<IModelProvider> providers,
        ModelReference source,
        MutationOptions options,
        StagingStore stagingStore,
        CliConnectionState? connection,
        CancellationToken cancellationToken)
    {
        var error = ResolveMode(options, out var mode);
        if (error is not null)
            return new MutationBegin(null, error);

        // Workspace mirror sync target, resolved from the active connection (suppressed by --no-sync).
        // --save-to writes a copy to a side location and leaves the connected source untouched, so
        // it must not deploy the mutation to the mirror either. The model-aware overload applies the
        // same reasoning to the source itself: a model addressed explicitly (path / --server /
        // --database) that is not the session's primary must never be deployed over the session's
        // mirror. Only consumed by the Save branch of CompleteAsync; harmless on other modes.
        var configuredSync = ActiveModelResolver.ResolveSyncTarget(connection, source);
        var syncSuppressed = options.NoSync || !string.IsNullOrWhiteSpace(options.SaveTo);
        var syncTarget = syncSuppressed ? null : configuredSync;

        if (mode is MutationMode.None or MutationMode.Save)
            return new MutationBegin(
                new MutationContext(mode, source, options.SaveTo, options.Serialization, options.Force, null, syncTarget, options.Overwrite,
                    options.DryRun, SyncSuppressed: syncSuppressed && configuredSync is not null),
                null);

        if (mode == MutationMode.Revert)
            return new MutationBegin(
                new MutationContext(mode, source, null, options.Serialization, options.Force, null, null),
                null);

        // Stage: redirect to a local working copy.
        StagingHandle handle;
        try
        {
            handle = await stagingStore.GetOrCreateAsync(source, connection, providers, cancellationToken);
        }
        catch (StagingManifestCorruptException ex)
        {
            return new MutationBegin(null, new MutationError(
                "TOMIX_STAGE_MANIFEST_CORRUPT", ex.Message, 2));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            return new MutationBegin(null, new MutationError(
                "TOMIX_STAGE_MATERIALIZE_FAILED", $"Could not create working copy: {ex.Message}", 2));
        }

        return new MutationBegin(
            new MutationContext(
                MutationMode.Stage,
                handle.WorkingCopyReference,
                SaveTarget: null,
                handle.Manifest.Serialization,
                Force: true,
                handle,
                SyncTarget: null),
            null);
    }

    /// <summary>Persists the just-applied mutation according to the resolved mode.</summary>
    public static async Task<MutationOutcome> CompleteAsync(
        IModelMutationSession mutator,
        IModelSession session,
        MutationContext context,
        SaveValidationBaseline? validationBaseline,
        string command,
        string summary,
        CancellationToken cancellationToken)
    {
        switch (context.Mode)
        {
            case MutationMode.Save:
                SaveValidationDelta? validation = null;
                if (validationBaseline is not null)
                {
                    validation = SaveValidation.Compare(
                        validationBaseline.Snapshot,
                        await session.GetSnapshotAsync(cancellationToken));
                    if (validationBaseline.Enforce && validation.NewErrorCount > 0)
                        throw new SaveValidationBlockedException(validation);
                }
                var export = await mutator.SaveAsync(context.SaveTarget, context.Serialization, context.Overwrite, cancellationToken);
                var sync = context.SyncSuppressed
                    ? new SyncOutcome(SyncStatus.Skipped)
                    : await WorkspaceSync.SyncAsync(
                        mutator, context.SyncTarget, context.Force,
                        WorkspaceSync.SyncOptionsFor(command), cancellationToken);
                var (savedTo, persistence) = Describe(context.EffectiveModel, context.SaveTarget, export.SavedPath);
                // A remote save reports the database it wrote to; carry it into the target.
                var target = persistence is PersistenceKind.File
                    ? null
                    : new MutationTarget(context.EffectiveModel.Value, context.EffectiveModel.Database ?? export.SavedPath, null);
                return new MutationOutcome(MutationStatus.Saved, savedTo, persistence, sync, target, validation);

            case MutationMode.Stage:
                // Flush the in-memory mutation into the working copy on disk, then record the op.
                await mutator.SaveAsync(null, context.Serialization, overwrite: true, cancellationToken);
                await context.Staging!.AppendOpAsync(command, summary, cancellationToken);
                return MutationOutcome.Staged;

            default:
                return context.DryRun ? MutationOutcome.DryRun : MutationOutcome.Preview;
        }
    }

    /// <summary>
    /// Where a save landed. <c>--save-to</c> always writes a file. Otherwise a local path is a
    /// file save; a <c>localhost</c> endpoint is the model inside Power BI Desktop, which keeps the
    /// change only in memory until the report is saved; any other endpoint is the service. The
    /// provider reports a remote save as the database name, so it is qualified with the server.
    /// </summary>
    internal static (string SavedTo, PersistenceKind Persistence) Describe(
        ModelReference model, string? saveTarget, string savedPath)
    {
        if (!string.IsNullOrWhiteSpace(saveTarget) || !model.IsRemote)
            return (savedPath, PersistenceKind.File);

        return ($"{model.Value} / {savedPath}",
            model.IsLocalInstance ? PersistenceKind.LiveModel : PersistenceKind.Service);
    }

}
