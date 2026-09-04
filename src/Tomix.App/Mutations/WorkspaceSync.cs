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
    /// <summary>The one command that owns refresh policies, and so must deploy them in full.</summary>
    private const string RefreshPolicyCommand = "incremental-refresh";

    /// <summary>
    /// Deploy options for a synced mutation made by <paramref name="command"/>.
    ///
    /// A sync overwrites the mirror, because the session's model came from that same workspace plus
    /// the user's mutations — preserving target objects would silently revert them. The single
    /// exemption is incremental-refresh policy partitions: those are generated and processed on the
    /// service, exist only on the mirror, and a local-primary model cannot recreate their data, so
    /// deploying them would discard processed history on every synced edit (issue #129).
    ///
    /// Invariant: <c>"incremental-refresh"</c> is the ONE command that owns refresh policies. It
    /// opts back into <see cref="ModelDeployOptions.Full"/> because the preserve path also clones
    /// the target's <c>refreshPolicy</c> back, which would revert the policy edit the user just
    /// made. A future command that edits refresh policies under a different command string would
    /// silently get the exempted options and lose its edit — add it here when that happens.
    ///
    /// Accepted residual gap: a generic <c>tx set</c>/<c>tx rm</c> edit to a policy table's
    /// partitions or refresh policy (not made through <c>incremental-refresh</c>) is preserved over
    /// on the mirror. <c>tx deploy --deploy-full</c> is the escape hatch.
    /// </summary>
    internal static ModelDeployOptions SyncOptionsFor(string command)
        => command == RefreshPolicyCommand
            ? ModelDeployOptions.Full
            : ModelDeployOptions.Full with { DeployPolicyPartitions = false };

    /// <summary>
    /// Deploy options for a batch of staged mutations; see <see cref="SyncOptionsFor(string)"/>.
    /// Any staged incremental-refresh op makes the whole commit a full deploy, since the policy
    /// edit must land.
    /// </summary>
    internal static ModelDeployOptions SyncOptionsFor(IEnumerable<string> commands)
        => commands.Contains(RefreshPolicyCommand, StringComparer.Ordinal)
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
