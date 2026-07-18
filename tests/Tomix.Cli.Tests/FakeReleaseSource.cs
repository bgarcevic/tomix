using Tomix.App.Update;
using Tomix.Core.Update;

namespace Tomix.Cli.Tests;

/// <summary>Configurable <see cref="IReleaseSource"/> for update command tests.</summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    public static readonly FakeReleaseSource Empty = new();

    public List<ReleaseInfo> Releases { get; init; } = [];

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
        => Task.FromResult(Releases.FirstOrDefault(release => !release.Prerelease));

    public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ReleaseInfo>>(Releases);

    public Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken)
        => Task.FromException<byte[]>(new HttpRequestException("no assets configured"));

    public Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken)
        => Task.FromResult("");
}
