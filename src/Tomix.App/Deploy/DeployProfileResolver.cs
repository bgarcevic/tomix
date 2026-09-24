using Tomix.App.State;
using Tomix.Core.Results;

namespace Tomix.App.Deploy;

/// <summary>Validates a profile explicitly selected as a deploy target.</summary>
public static class DeployProfileResolver
{
    public static TomixResult<CliProfile> Resolve(CliStateStore store, string name)
    {
        var profiles = store.LoadProfiles();
        if (!profiles.TryGetValue(name, out var profile))
            return TomixResult<CliProfile>.Fail(
                "TOMIX_PROFILE_NOT_FOUND",
                $"Profile '{name}' not found.",
                exitCode: 1,
                hint: "Run 'tx profile list' to see saved profiles, or 'tx profile set <name> -s <workspace> -d <database>' to create one.");

        if (string.IsNullOrWhiteSpace(profile.Server))
            return TomixResult<CliProfile>.Fail(
                "TOMIX_DEPLOY_PROFILE_NO_SERVER",
                $"Profile '{name}' has no server and cannot be used as a deploy target.",
                exitCode: 2,
                hint: "Create or update a remote profile with 'tx profile set <name> -s <workspace> -d <database>'.");

        return TomixResult<CliProfile>.Ok(profile);
    }
}
