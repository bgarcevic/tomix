using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>
/// Shared workspace-sync tail for save/deploy paths. When a sync target is resolved (an active
/// workspace mirror), the just-saved model is pushed to the remote via the session's
/// <see cref="IModelDeploySession"/>. Failures are surfaced as warnings rather than hard errors,
/// since the local save already succeeded.
/// </summary>
internal static class WorkspaceSync
{
    public static async Task<(bool Synced, string? Target, string? Warning)> SyncAsync(
        object session,
        ModelReference? syncTarget,
        bool force,
        CancellationToken cancellationToken)
    {
        if (syncTarget is null)
            return (false, null, null);

        if (session is not IModelDeploySession deployer)
            return (false, null, "Workspace sync skipped: provider does not support deploy.");

        var targetLabel = syncTarget.Database is not null
            ? $"{syncTarget.Value} / {syncTarget.Database}"
            : syncTarget.Value;

        MutationProgress.Report($"Syncing to {targetLabel}...");

        try
        {
            await deployer.DeployAsync(
                new ModelDeployRequest(syncTarget.Value, syncTarget.Database, CreateOnly: false, Force: force),
                cancellationToken);

            return (true, targetLabel, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, targetLabel,
                $"Workspace sync failed: {ex.Message} "
                + "The local save succeeded — run 'tx save' after fixing this to push the mirror, or use --no-sync to skip it.");
        }
    }
}
