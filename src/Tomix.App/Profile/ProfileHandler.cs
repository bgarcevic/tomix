using Tomix.App.State;
using Tomix.Core.Results;

namespace Tomix.App.Profile;

public sealed class ProfileHandler
{
    private readonly CliStateStore _store;

    public ProfileHandler(CliStateStore store) => _store = store;

    public TomixResult<ProfileListResult> List()
    {
        var profiles = _store.LoadProfiles()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return TomixResult<ProfileListResult>.Ok(new ProfileListResult(profiles));
    }

    public TomixResult<ProfileShowResult> Show(string name)
    {
        var profiles = _store.LoadProfiles();
        return profiles.TryGetValue(name, out var profile)
            ? TomixResult<ProfileShowResult>.Ok(new ProfileShowResult(profile))
            : TomixResult<ProfileShowResult>.Fail("TOMIX_PROFILE_NOT_FOUND", $"Profile not found: {name}", exitCode: 1);
    }

    public TomixResult<ProfileSetResult> Set(ProfileSetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TomixResult<ProfileSetResult>.Fail("TOMIX_PROFILE_NAME_REQUIRED", "Profile name is required.", exitCode: 2);

        // --from-active seeds the profile from the active connection; explicit
        // -s/-d/--model/--auth values still win over the session's.
        CliConnectionState? session = null;
        if (request.FromActive)
        {
            session = _store.LoadCurrentSession();
            if (session is null)
                return TomixResult<ProfileSetResult>.Fail(
                    "TOMIX_NO_ACTIVE_CONNECTION",
                    "No active connection to save. Run 'tx connect' first, or pass --server/--database explicitly.",
                    exitCode: 2);
        }

        var profiles = _store.LoadProfiles();
        var profile = new CliProfile(
            request.Name,
            request.Server ?? session?.Server,
            request.Database ?? session?.Database,
            request.Model ?? session?.Model,
            request.Auth ?? session?.Auth,
            request.Description,
            request.AutoFormat,
            request.ValidateOnMutation,
            request.BpaOnMutation,
            request.BpaOnDeploy,
            request.VertipaqOnRefresh,
            request.Spinner,
            session?.Workspace,
            session?.WorkspaceFormat,
            session?.WorkspaceAuth);

        profiles[request.Name] = profile;
        _store.SaveProfiles(profiles);
        return TomixResult<ProfileSetResult>.Ok(new ProfileSetResult(profile));
    }

    public TomixResult<ProfileRemoveResult> Remove(string name)
    {
        var profiles = _store.LoadProfiles();
        var removed = profiles.Remove(name);
        if (removed)
            _store.SaveProfiles(profiles);

        return TomixResult<ProfileRemoveResult>.Ok(new ProfileRemoveResult(name, removed));
    }
}

public sealed record ProfileSetRequest(
    string Name,
    string? Server,
    string? Database,
    string? Model,
    string? Auth,
    string? Description,
    bool? AutoFormat,
    bool? ValidateOnMutation,
    bool? BpaOnMutation,
    bool? BpaOnDeploy,
    bool? VertipaqOnRefresh,
    bool? Spinner,
    bool FromActive = false);
