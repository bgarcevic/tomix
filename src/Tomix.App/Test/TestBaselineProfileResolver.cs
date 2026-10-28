using Tomix.App.State;
using Tomix.Core.Results;

namespace Tomix.App.Test;

/// <summary>Validates a profile named with <c>tx test --baseline</c>: it must address a live model.</summary>
public static class TestBaselineProfileResolver
{
    public static TomixResult<CliProfile> Resolve(CliStateStore store, string name)
    {
        var profiles = store.LoadProfiles();
        if (!profiles.TryGetValue(name, out var profile))
            return TomixResult<CliProfile>.Fail(
                "TOMIX_PROFILE_NOT_FOUND",
                $"Profile '{name}' not found.",
                exitCode: 2,
                hint: "Run 'tx profile list' to see saved profiles, or 'tx profile set <name> -s <workspace> -d <database>' to create one.");

        if (string.IsNullOrWhiteSpace(profile.Server))
            return TomixResult<CliProfile>.Fail(
                "TOMIX_TEST_BASELINE_NO_SERVER",
                $"Profile '{name}' has no server and cannot be used as a baseline model.",
                exitCode: 2,
                hint: "A baseline is a live model: create or update the profile with 'tx profile set <name> -s <workspace> -d <database>'.");

        return TomixResult<CliProfile>.Ok(profile);
    }
}
