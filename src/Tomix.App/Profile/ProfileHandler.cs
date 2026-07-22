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
        profiles.TryGetValue(request.Name, out var existing);
        var server = request.Model is not null || request.Local == true
            ? null
            : request.Server ?? session?.Server ?? existing?.Server;
        var database = request.Database ?? session?.Database ?? existing?.Database;
        var model = request.Server is not null || request.Local == true
            ? null
            : request.Model ?? session?.Model ?? existing?.Model;
        var local = request.Local
            ?? (request.Model is not null ? true : (bool?)null)
            ?? (request.Server is not null ? false : (bool?)null)
            ?? session?.Local
            ?? existing?.Local
            ?? !string.IsNullOrWhiteSpace(model);

        if (existing is null && string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(model) && !local)
            return TomixResult<ProfileSetResult>.Fail(
                "TOMIX_PROFILE_TARGET_REQUIRED",
                "A new profile requires --server, --model, or --from-active.",
                exitCode: 2);

        var profile = new CliProfile(
            request.Name,
            server,
            database,
            model,
            request.Auth ?? session?.Auth ?? existing?.Auth,
            request.Description ?? existing?.Description,
            local,
            session?.Workspace ?? existing?.Workspace,
            session?.WorkspaceFormat ?? existing?.WorkspaceFormat,
            session?.WorkspaceAuth ?? existing?.WorkspaceAuth);

        profiles[request.Name] = profile;
        _store.SaveProfiles(profiles);
        return TomixResult<ProfileSetResult>.Ok(new ProfileSetResult(profile));
    }

    public TomixResult<ProfileResolveResult> Resolve(string name)
    {
        var profiles = _store.LoadProfiles();
        if (!profiles.TryGetValue(name, out var profile))
            return TomixResult<ProfileResolveResult>.Fail(
                "TOMIX_PROFILE_NOT_FOUND", $"Profile '{name}' not found", exitCode: 1);

        var normalized = profile with
        {
            Local = profile.Local || !string.IsNullOrWhiteSpace(profile.Model)
        };

        if (string.IsNullOrWhiteSpace(normalized.Server) &&
            string.IsNullOrWhiteSpace(normalized.Model) &&
            !normalized.Local)
            return TomixResult<ProfileResolveResult>.Fail(
                "TOMIX_PROFILE_TARGET_REQUIRED",
                $"Profile '{name}' has no usable connection target.",
                exitCode: 2);

        return TomixResult<ProfileResolveResult>.Ok(new ProfileResolveResult(normalized));
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
    bool? Local,
    bool FromActive = false);
