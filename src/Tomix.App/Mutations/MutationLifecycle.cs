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
    bool NoSync = false);

/// <summary>Where a handler should open/mutate and how it should persist, resolved up front by <see cref="MutationLifecycle"/>.</summary>
public sealed record MutationContext(
    MutationMode Mode,
    ModelReference EffectiveModel,
    string? SaveTarget,
    string Serialization,
    bool Force,
    StagingHandle? Staging,
    ModelReference? SyncTarget = null);

/// <summary>A failed pre-flight: the code/message/exit-code the handler should return verbatim.</summary>
public sealed record MutationError(string Code, string Message, int ExitCode);

public sealed record MutationBegin(MutationContext? Context, MutationError? Error)
{
    public MutationMode Mode => Context?.Mode ?? MutationMode.None;
}

public sealed record MutationOutcome(
    object Saved,
    bool? Staged,
    bool Synced = false,
    string? SyncTarget = null,
    string? SyncWarning = null);

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
        // it must not deploy the mutation to the mirror either. Only consumed by the Save branch
        // of CompleteAsync; harmless on other modes.
        var syncTarget = options.NoSync || !string.IsNullOrWhiteSpace(options.SaveTo)
            ? null
            : ResolveSyncTarget(connection);

        if (mode is MutationMode.None or MutationMode.Save)
            return new MutationBegin(
                new MutationContext(mode, source, options.SaveTo, options.Serialization, options.Force, null, syncTarget),
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
        MutationContext context,
        string command,
        string summary,
        CancellationToken cancellationToken)
    {
        switch (context.Mode)
        {
            case MutationMode.Save:
                var export = await mutator.SaveAsync(context.SaveTarget, context.Serialization, context.Force, cancellationToken);
                var (synced, syncTarget, syncWarning) = await WorkspaceSync.SyncAsync(
                    mutator, context.SyncTarget, context.Force, cancellationToken);
                return new MutationOutcome(export.SavedPath, null, synced, syncTarget, syncWarning);

            case MutationMode.Stage:
                // Flush the in-memory mutation into the working copy on disk, then record the op.
                await mutator.SaveAsync(null, context.Serialization, force: true, cancellationToken);
                await context.Staging!.AppendOpAsync(command, summary, cancellationToken);
                return new MutationOutcome(false, true);

            default:
                return new MutationOutcome(false, null);
        }
    }

    /// <summary>
    /// Resolves the remote workspace sync target from the active connection, mirroring
    /// <c>ActiveModelResolver.ResolveSyncTarget</c>. A remote workspace endpoint wins; otherwise
    /// the primary server is used. Returns null when there is no mirror configured.
    /// </summary>
    private static ModelReference? ResolveSyncTarget(CliConnectionState? connection)
    {
        if (connection is null || string.IsNullOrWhiteSpace(connection.Workspace))
            return null;

        if (ModelReference.IsRemoteEndpoint(connection.Workspace))
            return new ModelReference(connection.Workspace, NullIfBlank(connection.Database));

        if (!string.IsNullOrWhiteSpace(connection.Server))
            return new ModelReference(connection.Server, NullIfBlank(connection.Database));

        return null;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
