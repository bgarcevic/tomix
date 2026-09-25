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
    /// <summary>
    /// Policy mutations must deploy the edited policy; ordinary edits preserve server-generated
    /// partitions. The legacy marker remains recognized for operations staged before migration.
    /// </summary>
    internal static ModelDeployOptions SyncOptionsFor(string command)
        => command is "refresh-policy" or "incremental-refresh"
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };

    /// <summary>Includes policy edits anywhere in a staged batch.</summary>
    internal static ModelDeployOptions SyncOptionsFor(IEnumerable<string> commands)
        => commands.Any(command => command is "refresh-policy" or "incremental-refresh")
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };

    public static async Task<(bool Synced, string? Target, string? Warning)> SyncAsync(
        object session,
        ModelReference? syncTarget,
        bool force,
        ModelDeployOptions options,
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
                // Overwrite the mirror — the session's model came from this same workspace plus the
                // user's mutations, so preserving target objects would silently revert them — except
                // for the policy-partition exemption chosen by SyncOptionsFor.
                new ModelDeployRequest(syncTarget.Value, syncTarget.Database, CreateOnly: false, Force: force,
                    Options: options),
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
