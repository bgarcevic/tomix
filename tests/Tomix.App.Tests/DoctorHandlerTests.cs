using Tomix.App.Doctor;
using Tomix.App.Update;
using Tomix.Core.Doctor;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

public sealed class DoctorHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-doctor-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.1.0-alpha.1")]
    [InlineData("2.3.4-beta.5")]
    public void Handle_ReturnsDoctorResult(string version)
    {
        var handler = new DoctorHandler(_dir);

        var result = handler.Handle(version);

        Assert.NotNull(result.Data);
        Assert.Equal(version, result.Data.Version);
        Assert.NotEmpty(result.Data.Checks);
    }

    [Fact]
    public void Handle_IncludesRuntimeCheck()
    {
        var handler = new DoctorHandler(_dir);

        var result = handler.Handle("1.0.0");

        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "runtime" &&
            check.Status == DoctorCheckStatus.Pass);
    }

    [Fact]
    public void Handle_ReportsTheInjectedConfigDirectory()
    {
        var handler = new DoctorHandler(_dir);

        var result = handler.Handle("1.0.0");

        Assert.Equal(_dir, result.Data!.ConfigDirectory);
        Assert.Contains(result.Data.Checks, check =>
            check.Name == "config-directory" &&
            check.Status == DoctorCheckStatus.Pass &&
            check.Message == _dir);
    }

    [Fact]
    public void Handle_WithoutReleaseSource_SkipsUpdateCheck()
    {
        var handler = new DoctorHandler(_dir);

        var result = handler.Handle("1.0.0");

        Assert.Null(result.Data!.LatestVersion);
        Assert.DoesNotContain(result.Data.Checks, check => check.Name == "update");
    }

    [Fact]
    public void Handle_WarnsWhenANewerReleaseExists()
    {
        var handler = new DoctorHandler(_dir, new FakeReleaseSource("2.0.0"), InstallKind.Standalone);

        var result = handler.Handle("1.0.0");

        Assert.Equal("2.0.0", result.Data!.LatestVersion);
        Assert.Contains(result.Data.Checks, check =>
            check.Name == "update" &&
            check.Status == DoctorCheckStatus.Warning &&
            check.Message.Contains("2.0.0"));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Handle_PassesWhenUpToDate()
    {
        var handler = new DoctorHandler(_dir, new FakeReleaseSource("1.0.0"), InstallKind.Standalone);

        var result = handler.Handle("1.0.0");

        Assert.Equal("1.0.0", result.Data!.LatestVersion);
        Assert.Contains(result.Data.Checks, check =>
            check.Name == "update" &&
            check.Status == DoctorCheckStatus.Pass);
    }

    [Fact]
    public void Handle_WarnsWithoutFailingWhenTheLookupThrows()
    {
        var handler = new DoctorHandler(_dir, new FakeReleaseSource(latestVersion: null, throws: true), InstallKind.Standalone);

        var result = handler.Handle("1.0.0");

        Assert.Null(result.Data!.LatestVersion);
        Assert.Contains(result.Data.Checks, check =>
            check.Name == "update" &&
            check.Status == DoctorCheckStatus.Warning);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Handle_SkipsUpdateCheckInDevelopmentInstalls()
    {
        var handler = new DoctorHandler(_dir, new FakeReleaseSource("2.0.0"), InstallKind.Development);

        var result = handler.Handle("1.0.0");

        Assert.Null(result.Data!.LatestVersion);
        Assert.DoesNotContain(result.Data.Checks, check => check.Name == "update");
    }

    private sealed class FakeReleaseSource(string? latestVersion, bool throws = false) : IReleaseSource
    {
        public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
            => throws
                ? Task.FromException<ReleaseInfo?>(new HttpRequestException("offline"))
                : Task.FromResult(latestVersion is null
                    ? null
                    : new ReleaseInfo(latestVersion, null, null, null, Prerelease: false));

        public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

        public Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
