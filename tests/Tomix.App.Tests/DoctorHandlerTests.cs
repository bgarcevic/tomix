using Tomix.App.Config;
using Tomix.App.Doctor;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Core.Doctor;

namespace Tomix.App.Tests;

public sealed class DoctorHandlerTests : IDisposable
{
    private static readonly DoctorTerminalCapabilities Terminal = new(true, true, "TrueColor");
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-doctor-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DoctorHandler CreateHandler(
        string? directory = null,
        IReadOnlyList<string>? providers = null,
        string? configLoadError = null)
    {
        var dir = directory ?? _dir;
        return new DoctorHandler(
            dir,
            new TomixConfigStore(Path.Combine(dir, "config.json")),
            new CliStateStore(dir),
            new UpdateCheckStore(dir),
            Path.Combine(dir, "auth", "auth-state.json"),
            providers ?? ["FakeModelProvider"],
            configLoadError);
    }

    [Fact]
    public void Handle_ReportsEveryLocalCheckAndTerminalParity()
    {
        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Terminal, result.Data!.Terminal);
        Assert.All(
            new[] { "runtime", "operating-system", "config-directory", "configuration", "profiles", "sessions", "authentication", "model-providers", "terminal", "update-cache" },
            name => Assert.Contains(result.Data.Checks, check => check.Name == name));
    }

    [Fact]
    public void Handle_WarningsDoNotFail()
    {
        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Contains(result.Data!.Checks, check => check.Status == DoctorCheckStatus.Warning);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Handle_UsesCachedUpdateInformationOnly()
    {
        new UpdateCheckStore(_dir).Save("2.0.0");

        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Equal("2.0.0", result.Data!.LatestVersion);
        Assert.Contains(result.Data.Checks, check =>
            check.Name == "update-cache" && check.Status == DoctorCheckStatus.Warning);
    }

    [Fact]
    public void Handle_CorruptStartupConfigFailsHealthCheck()
    {
        var result = CreateHandler(configLoadError: "Config file is corrupt").Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "configuration" && check.Status == DoctorCheckStatus.Fail);
    }

    [Fact]
    public void Handle_InvalidProfilesSessionsAndAuthMetadataFail()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), "not json");
        Directory.CreateDirectory(Path.Combine(_dir, "sessions"));
        File.WriteAllText(Path.Combine(_dir, "sessions", "named.json"), "not json");
        Directory.CreateDirectory(Path.Combine(_dir, "auth"));
        File.WriteAllText(Path.Combine(_dir, "auth", "auth-state.json"), "not json");

        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.All(
            new[] { "profiles", "sessions", "authentication" },
            name => Assert.Contains(result.Data!.Checks, check =>
                check.Name == name && check.Status == DoctorCheckStatus.Fail));
    }

    [Fact]
    public void Handle_NoRegisteredProvidersFails()
    {
        var result = CreateHandler(providers: []).Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "model-providers" && check.Status == DoctorCheckStatus.Fail);
    }

    [Fact]
    public void Handle_UnwritableConfigPathFails()
    {
        var path = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(path, "file");

        var result = CreateHandler(directory: path).Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "config-directory" && check.Status == DoctorCheckStatus.Fail);
    }
}
