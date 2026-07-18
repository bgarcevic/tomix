using Tomix.App.Update;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

/// <summary>Configurable <see cref="IReleaseSource"/> for update handler tests.</summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    public List<ReleaseInfo> Releases { get; init; } = [];

    /// <summary>Assets keyed by <c>"{version}/{assetName}"</c>.</summary>
    public Dictionary<string, byte[]> Assets { get; init; } = [];

    public string ChecksumsText { get; init; } = "";

    public Exception? ThrowOnList { get; init; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
        => Task.FromResult(Releases.FirstOrDefault(release => !release.Prerelease));

    public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken)
        => ThrowOnList is null
            ? Task.FromResult<IReadOnlyList<ReleaseInfo>>(Releases)
            : Task.FromException<IReadOnlyList<ReleaseInfo>>(ThrowOnList);

    public Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken)
        => Assets.TryGetValue($"{version}/{assetName}", out var bytes)
            ? Task.FromResult(bytes)
            : Task.FromException<byte[]>(new HttpRequestException($"404: {version}/{assetName}"));

    public Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken)
        => Task.FromResult(ChecksumsText);
}
