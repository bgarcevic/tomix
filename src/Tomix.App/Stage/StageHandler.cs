using System.Diagnostics;
using Tomix.App.State;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Stage;

/// <summary>Inspects and clears the session's staged working copies (the <c>tx stage</c> surface).</summary>
public sealed class StageHandler
{
    private readonly StagingStore _staging;

    public StageHandler()
        : this(new StagingStore())
    {
    }

    public StageHandler(StagingStore staging) => _staging = staging;

    public TomixResult<StageStatusResult> Status(ModelReference source)
    {
        StagingInfo? info;
        try
        {
            info = _staging.TryLoad(source);
        }
        catch (StagingManifestCorruptException ex)
        {
            return CorruptManifest<StageStatusResult>(ex);
        }

        if (info is null)
            return TomixResult<StageStatusResult>.Ok(new StageStatusResult(
                Staged: false, source.Value, WorkingCopy: null, Serialization: null,
                Workspace: false, OpCount: 0, Ops: [], CreatedUtc: null, UpdatedUtc: null));

        var manifest = info.Manifest;
        return TomixResult<StageStatusResult>.Ok(new StageStatusResult(
            Staged: true,
            manifest.Source,
            manifest.WorkingCopy,
            manifest.Serialization,
            Workspace: manifest.Workspace is not null,
            manifest.Ops.Count,
            manifest.Ops,
            manifest.CreatedUtc,
            manifest.UpdatedUtc));
    }

    public TomixResult<StageListResult> List()
    {
        try
        {
            var entries = _staging.List()
                .Select(info => new StageListEntry(
                    info.Manifest.Source,
                    info.Manifest.WorkingCopy,
                    info.Manifest.Ops.Count,
                    info.Manifest.UpdatedUtc,
                    info.IsCurrentSession))
                .ToList();
            return TomixResult<StageListResult>.Ok(new StageListResult(entries));
        }
        catch (StagingManifestCorruptException ex)
        {
            return CorruptManifest<StageListResult>(ex);
        }
    }

    public TomixResult<StageDiscardResult> Discard(ModelReference source, bool all)
    {
        var discarded = all ? _staging.DiscardAll() : (_staging.Discard(source) ? 1 : 0);
        return TomixResult<StageDiscardResult>.Ok(new StageDiscardResult(discarded));
    }

    /// <summary>
    /// Promotes the staged working copy onto its source. The local side is always written; the remote
    /// side (workspace mirror) is synced separately. The manifest carries the local target and
    /// (when in workspace mode) the remote endpoint captured at stage time.
    /// </summary>
    public async Task<TomixResult<StageCommitResult>> CommitAsync(
        ModelReference source,
        IReadOnlyList<IModelProvider> providers,
        bool force,
        CancellationToken cancellationToken)
    {
        StagingInfo? info;
        try
        {
            info = _staging.TryLoad(source);
        }
        catch (StagingManifestCorruptException ex)
        {
            return CorruptManifest<StageCommitResult>(ex);
        }

        if (info is null)
            return TomixResult<StageCommitResult>.Fail(
                "TOMIX_STAGE_NOTHING_TO_COMMIT", $"Nothing staged to commit for {source.Value}.", 1);

        var manifest = info.Manifest;

        if (!force && _staging.HasSourceDrifted(manifest, source))
            return TomixResult<StageCommitResult>.Fail(
                "TOMIX_STAGE_SOURCE_DRIFT",
                $"Source '{manifest.Source}' changed since staging began. Re-stage, or commit with --force to overwrite.",
                1);

        var workingReference = new ModelReference(manifest.WorkingCopy);
        var provider = providers.ResolveSingle(workingReference);
        if (provider is null)
            return TomixResult<StageCommitResult>.Fail(
                "TOMIX_NO_PROVIDER", $"No provider can open working copy: {manifest.WorkingCopy}", 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        bool remoteDeployed = false;
        string? deployServer = null;
        string? deployDatabase = null;
        long? deployDurationMs = null;

        if (manifest.SourceKind == "remote"
            && !string.IsNullOrWhiteSpace(manifest.SourceEndpoint))
        {
            await using var session = await provider.OpenAsync(workingReference, cancellationToken);
            if (session is IModelDeploySession deployer)
            {
                var sw = Stopwatch.StartNew();
                var deployResult = await deployer.DeployAsync(
                    new ModelDeployRequest(
                        manifest.SourceEndpoint,
                        manifest.SourceDatabase,
                        DeployFull: false,
                        CreateOnly: false,
                        Force: force),
                    cancellationToken);
                sw.Stop();

                remoteDeployed = true;
                deployServer = deployResult.Server;
                deployDatabase = deployResult.Database;
                deployDurationMs = sw.ElapsedMilliseconds;
            }
            else
            {
                return TomixResult<StageCommitResult>.Fail(
                    "TOMIX_STAGE_COMMIT_REMOTE_FAILED",
                    $"Working copy provider cannot deploy: {manifest.WorkingCopy}", 2,
                    hint: "Ensure the working copy is in a TOM-compatible format.");
            }
        }
        else
        {
            string localSaved;
            try
            {
                await using var session = await provider.OpenAsync(workingReference, cancellationToken);
                if (session is not IModelExportSession exporter)
                    return TomixResult<StageCommitResult>.Fail(
                        "TOMIX_STAGE_COMMIT_LOCAL_FAILED", $"Working copy cannot be exported: {manifest.WorkingCopy}", 2);

                var export = await exporter.ExportAsync(
                    new ModelExportRequest(manifest.Source, manifest.Serialization, Force: true, SupportingFiles: false),
                    cancellationToken);
                localSaved = export.SavedPath;
            }
            catch (IOException ex)
            {
                return TomixResult<StageCommitResult>.Fail(
                    "TOMIX_STAGE_COMMIT_LOCAL_FAILED", $"Failed to write local side: {ex.Message}. Staging kept.", 2);
            }
        }

        _staging.Discard(source);
        return TomixResult<StageCommitResult>.Ok(new StageCommitResult(
            manifest.Source, null, remoteDeployed,
            deployServer, deployDatabase, deployDurationMs, manifest.Ops.Count));
    }

    // Every stage command must surface a corrupt manifest as the documented diagnostic
    // (exit 2 + discard hint), not as an unhandled exception from Program's catch-all.
    private static TomixResult<T> CorruptManifest<T>(StagingManifestCorruptException ex)
        => TomixResult<T>.Fail("TOMIX_STAGE_MANIFEST_CORRUPT", ex.Message, 2);
}
