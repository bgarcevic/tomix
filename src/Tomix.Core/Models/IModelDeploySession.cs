namespace Tomix.Core.Models;

public interface IModelDeploySession
{
    Task<ModelDeployResult> DeployAsync(
        ModelDeployRequest request,
        CancellationToken cancellationToken);

    string GenerateScript(ModelDeployRequest request);
}

public sealed record ModelDeployRequest(
    string Server,
    string? Database,
    bool DeployFull,
    bool CreateOnly,
    bool Force);

public sealed record ModelDeployResult(
    string Server,
    string Database,
    string Status,
    long DurationMs);
