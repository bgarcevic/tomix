namespace Tomix.Core.Models;

public interface IModelDeploySession
{
    Task<ModelDeployResult> DeployAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates the deployment script. When <see cref="ModelDeployRequest.Options"/> preserves
    /// any target-owned objects, this reads the target database so the script matches what
    /// <see cref="DeployAsync"/> would execute; otherwise it is generated offline.
    /// </summary>
    Task<string> GenerateScriptAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Controls which aspects of an existing target database are overwritten by a deploy.
/// Every flag defaults to <c>false</c>, meaning the target's current objects are preserved.
/// Preservation only applies when the target database already exists; a first deploy always
/// ships the full source model.
/// </summary>
public sealed record ModelDeployOptions(
    bool DeployConnections = false,
    bool DeployPartitions = false,
    bool DeploySharedExpressions = false,
    bool DeployRoles = false,
    bool DeployRoleMembers = false,
    bool DeployPolicyPartitions = false)
{
    /// <summary>Preserve everything the target owns (the safe default for promotion deploys).</summary>
    public static ModelDeployOptions Preserve { get; } = new();

    /// <summary>Overwrite everything from the source, including incremental-refresh partitions.</summary>
    public static ModelDeployOptions Full { get; } = new(
        DeployConnections: true,
        DeployPartitions: true,
        DeploySharedExpressions: true,
        DeployRoles: true,
        DeployRoleMembers: true,
        DeployPolicyPartitions: true);

    /// <summary>True when any aspect is preserved, requiring the target database to be read.</summary>
    public bool RequiresTargetRead =>
        !(DeployConnections && DeployPartitions && DeploySharedExpressions
          && DeployRoles && DeployRoleMembers && DeployPolicyPartitions);
}

public sealed record ModelDeployRequest(
    string Server,
    string? Database,
    bool CreateOnly,
    bool Force,
    ModelDeployOptions? Options = null)
{
    /// <summary>Effective options; a null <see cref="Options"/> means preserve-by-default.</summary>
    public ModelDeployOptions EffectiveOptions => Options ?? ModelDeployOptions.Preserve;
}

public sealed record ModelDeployResult(
    string Server,
    string Database,
    string Status,
    long DurationMs);
