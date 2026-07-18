using Tomix.App.Update;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

public sealed class UpdateCheckHandlerTests
{
    private static ReleaseInfo Release(string version, string? body = null, bool prerelease = false)
        => new(version, Name: $"v{version}", Body: body, PublishedAt: null, Prerelease: prerelease);

    private static Task<Tomix.Core.Results.TomixResult<UpdateCheckResult>> Handle(
        FakeReleaseSource source,
        string current = "0.1.0",
        string? target = null,
        InstallKind kind = InstallKind.Standalone)
        => new UpdateCheckHandler(source).HandleAsync(current, kind, target, CancellationToken.None);

    [Fact]
    public async Task ListsReleasesBetweenInstalledAndLatest_NewestFirst()
    {
        var source = new FakeReleaseSource
        {
            Releases =
            [
                Release("0.3.0", "* feat: shiny"),
                Release("0.2.0", "* feat(cli)!: rename option"),
                Release("0.1.0", "* initial"),
            ]
        };

        var result = await Handle(source, current: "0.1.0");

        Assert.NotNull(result.Data);
        Assert.True(result.Data.UpdateAvailable);
        Assert.Equal("0.3.0", result.Data.LatestVersion);
        Assert.Equal(["0.3.0", "0.2.0"], result.Data.Releases.Select(release => release.Version));
        Assert.False(result.Data.Releases[0].Breaking);
        Assert.True(result.Data.Releases[1].Breaking);
    }

    [Fact]
    public async Task SkipsPrereleases_ForStableInstalls()
    {
        var source = new FakeReleaseSource
        {
            Releases =
            [
                Release("0.3.0-rc.1", prerelease: true),
                Release("0.2.0"),
            ]
        };

        var result = await Handle(source, current: "0.1.0");

        Assert.Equal("0.2.0", result.Data!.LatestVersion);
        Assert.Equal(["0.2.0"], result.Data.Releases.Select(release => release.Version));
    }

    [Fact]
    public async Task IncludesPrereleases_WhenTheInstalledBuildIsAPrerelease()
    {
        var source = new FakeReleaseSource
        {
            Releases =
            [
                Release("0.2.0-rc.2", prerelease: true),
                Release("0.1.0"),
            ]
        };

        var result = await Handle(source, current: "0.2.0-rc.1");

        Assert.Equal("0.2.0-rc.2", result.Data!.LatestVersion);
        Assert.True(result.Data.UpdateAvailable);
    }

    [Fact]
    public async Task FlagsMajorVersionBumpAsBreaking_EvenWithBenignNotes()
    {
        var source = new FakeReleaseSource { Releases = [Release("1.0.0", "* feat: nothing scary")] };

        var result = await Handle(source, current: "0.9.0");

        Assert.True(Assert.Single(result.Data!.Releases).Breaking);
    }

    [Fact]
    public async Task ReportsUpToDate_WhenInstalledIsLatest()
    {
        var source = new FakeReleaseSource { Releases = [Release("0.3.0")] };

        var result = await Handle(source, current: "0.3.0");

        Assert.False(result.Data!.UpdateAvailable);
        Assert.Empty(result.Data.Releases);
    }

    [Fact]
    public async Task PinnedVersion_ResolvesThatRelease()
    {
        var source = new FakeReleaseSource
        {
            Releases = [Release("0.3.0"), Release("0.2.0"), Release("0.1.0")]
        };

        var result = await Handle(source, current: "0.1.0", target: "0.2.0");

        Assert.Equal("0.2.0", result.Data!.LatestVersion);
        Assert.Equal(["0.2.0"], result.Data.Releases.Select(release => release.Version));
    }

    [Fact]
    public async Task PinnedDowngrade_IsNotAnAvailableUpdate()
    {
        var source = new FakeReleaseSource { Releases = [Release("0.3.0"), Release("0.2.0")] };

        var result = await Handle(source, current: "0.3.0", target: "0.2.0");

        Assert.False(result.Data!.UpdateAvailable);
        Assert.Equal("0.2.0", result.Data.LatestVersion);
    }

    [Fact]
    public async Task PinnedVersionNotPublished_FailsWithVersionNotFound()
    {
        var source = new FakeReleaseSource { Releases = [Release("0.3.0")] };

        var result = await Handle(source, target: "9.9.9");

        Assert.Null(result.Data);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_VERSION_NOT_FOUND");
    }

    [Fact]
    public async Task NetworkFailure_FailsWithCheckFailed()
    {
        var source = new FakeReleaseSource { ThrowOnList = new HttpRequestException("offline") };

        var result = await Handle(source);

        Assert.Null(result.Data);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_CHECK_FAILED");
    }

    [Fact]
    public async Task NoReleases_FailsWithCheckFailed()
    {
        var result = await Handle(new FakeReleaseSource());

        Assert.Null(result.Data);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_CHECK_FAILED");
    }

    [Fact]
    public async Task SuccessfulCheck_RefreshesTheNoticeCache()
    {
        var dir = Directory.CreateTempSubdirectory("tomix-update-handler-tests").FullName;
        try
        {
            var store = new UpdateCheckStore(dir);
            var source = new FakeReleaseSource { Releases = [Release("0.3.0")] };
            var handler = new UpdateCheckHandler(source, store);

            var result = await handler.HandleAsync("0.1.0", InstallKind.Standalone, null, CancellationToken.None);

            Assert.NotNull(result.Data);
            Assert.Equal("0.3.0", store.Load()?.LatestVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PinnedCheck_CachesTheNewestStableRelease_NotThePin()
    {
        var dir = Directory.CreateTempSubdirectory("tomix-update-handler-tests").FullName;
        try
        {
            var store = new UpdateCheckStore(dir);
            var source = new FakeReleaseSource
            {
                Releases = [Release("0.4.0-rc.1", prerelease: true), Release("0.3.0"), Release("0.2.0")]
            };
            var handler = new UpdateCheckHandler(source, store);

            var result = await handler.HandleAsync("0.1.0", InstallKind.Standalone, "0.2.0", CancellationToken.None);

            // The check resolves the pin, but the notice cache must keep tracking the
            // newest stable release or the 0.3.0 notice would be suppressed for a day.
            Assert.Equal("0.2.0", result.Data!.LatestVersion);
            Assert.Equal("0.3.0", store.Load()?.LatestVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
