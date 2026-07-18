using Tomix.Core.Update;

namespace Tomix.App.Update;

/// <summary>
/// Where published CLI releases come from. The production implementation is
/// <see cref="GitHubReleaseSource"/>; tests substitute fakes.
/// </summary>
public interface IReleaseSource
{
    /// <summary>The latest stable release, or null when none exists.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>All published releases, newest first, prereleases included (flagged).</summary>
    Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a release asset (e.g. <c>tx-osx-arm64.tar.gz</c>) for the given version.</summary>
    Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken);

    /// <summary>Downloads the <c>checksums.txt</c> published with the given version.</summary>
    Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken);
}
