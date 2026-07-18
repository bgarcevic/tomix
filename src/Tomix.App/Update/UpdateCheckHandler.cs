using Tomix.Core.Results;
using Tomix.Core.Update;

namespace Tomix.App.Update;

/// <summary>
/// Resolves what an update would install: the target version (latest stable, or a pinned
/// <c>--version</c>) and the release notes for every version between installed and target,
/// with breaking-change flags.
/// </summary>
public sealed class UpdateCheckHandler
{
    private readonly IReleaseSource _source;
    private readonly UpdateCheckStore? _store;

    /// <param name="store">When present, a successful check refreshes the throttled-notice cache.</param>
    public UpdateCheckHandler(IReleaseSource source, UpdateCheckStore? store = null)
    {
        _source = source;
        _store = store;
    }

    public async Task<TomixResult<UpdateCheckResult>> HandleAsync(
        string currentVersion,
        InstallKind installKind,
        string? targetVersion,
        CancellationToken cancellationToken)
    {
        if (!CliVersion.TryParse(currentVersion, out var current))
        {
            return TomixResult<UpdateCheckResult>.Fail(
                code: "TOMIX_UPDATE_CHECK_FAILED",
                message: $"Cannot determine the installed version ('{currentVersion}').",
                exitCode: 1);
        }

        IReadOnlyList<ReleaseInfo> releases;
        try
        {
            releases = await _source.ListReleasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return TomixResult<UpdateCheckResult>.Fail(
                code: "TOMIX_UPDATE_CHECK_FAILED",
                message: $"Could not list releases: {ex.Message}",
                exitCode: 1,
                hint: "Check connectivity to api.github.com and retry.");
        }

        var parsed = releases
            .Select(release => CliVersion.TryParse(release.Version, out var version) ? (release, version) : default)
            .Where(pair => pair.release is not null)
            .OrderByDescending(pair => pair.version)
            .ToList();

        if (parsed.Count == 0)
        {
            return TomixResult<UpdateCheckResult>.Fail(
                code: "TOMIX_UPDATE_CHECK_FAILED",
                message: "No published releases were found.",
                exitCode: 1,
                hint: "Check connectivity to api.github.com and retry.");
        }

        // Prereleases only count when the installed build is itself a prerelease.
        var includePrereleases = current.Prerelease is not null;

        CliVersion target;
        if (targetVersion is not null)
        {
            if (!CliVersion.TryParse(targetVersion, out var pinned)
                || !parsed.Any(pair => pair.version.CompareTo(pinned) == 0))
            {
                return TomixResult<UpdateCheckResult>.Fail(
                    code: "TOMIX_UPDATE_VERSION_NOT_FOUND",
                    message: $"Version '{targetVersion}' is not a published release.",
                    exitCode: 2,
                    hint: "Run 'tx update --check' to list available versions.");
            }

            target = pinned;
        }
        else
        {
            var latest = parsed.FirstOrDefault(pair => includePrereleases || !pair.release.Prerelease).version;
            if (latest is null)
            {
                return TomixResult<UpdateCheckResult>.Fail(
                    code: "TOMIX_UPDATE_CHECK_FAILED",
                    message: "No stable release is available yet.",
                    exitCode: 1);
            }

            target = latest;
        }

        var between = parsed
            .Where(pair => pair.version.IsNewerThan(current) && pair.version.CompareTo(target) <= 0)
            .Where(pair => includePrereleases || !pair.release.Prerelease)
            .Select(pair => new ReleaseSummary(
                Version: pair.release.Version,
                PublishedAt: pair.release.PublishedAt,
                Breaking: BreakingChangeDetector.IsBreaking(pair.release.Body) || pair.version.Major > current.Major,
                Notes: pair.release.Body))
            .ToList();

        var result = new UpdateCheckResult(
            CurrentVersion: currentVersion,
            LatestVersion: target.ToString(),
            UpdateAvailable: target.IsNewerThan(current),
            InstallKind: installKind,
            Releases: between);

        // An explicit check is at least as good as the throttled one: reset its 24h clock.
        // Always cache the newest stable release, never the --version pin — caching an older
        // pinned target would suppress the notice for the real latest release for a day.
        var latestStable = parsed.FirstOrDefault(pair => !pair.release.Prerelease).version;
        if (latestStable is not null)
        {
            try
            {
                _store?.Save(latestStable.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cache refresh is best-effort; the check result stands on its own.
            }
        }

        return TomixResult<UpdateCheckResult>.Ok(result);
    }
}
