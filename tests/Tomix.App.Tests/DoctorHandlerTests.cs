using Tomix.App.Config;
using Tomix.App.Doctor;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Core.Doctor;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

public sealed class DoctorHandlerTests : IDisposable
{
    private static readonly DoctorTerminalCapabilities Terminal = new(true, true, "TrueColor");
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-doctor-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DoctorHandler CreateHandler(
        string? directory = null,
        string? configLoadError = null,
        bool tokenCacheOnDisk = true,
        CliStateStore? state = null,
        InstallKind installKind = InstallKind.DotnetTool,
        string? homeDirectory = null)
    {
        var dir = directory ?? _dir;
        return new DoctorHandler(
            dir,
            new TomixConfigStore(Path.Combine(dir, "config.json")),
            state ?? new CliStateStore(dir, currentSessionId: "doctor-test"),
            new UpdateCheckStore(dir),
            Path.Combine(dir, "auth"),
            configLoadError,
            tokenCacheOnDisk,
            installKind,
            // A home that contains nothing under test, so only the redaction tests see '~'.
            homeDirectory ?? Path.Combine(_dir, "no-home"));
    }

    private static DoctorCheck Check(DoctorResult result, string name)
        => Assert.Single(result.Checks, check => check.Name == name);

    [Fact]
    public void Handle_ReportsEveryLocalCheckAndTerminalParity()
    {
        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Terminal, result.Data!.Terminal);
        Assert.Equal(
            ["config-directory", "configuration", "profiles", "sessions", "current-session", "authentication", "update-cache"],
            result.Data.Checks.Select(check => check.Name));
    }

    [Fact]
    public void Handle_FreshInstallHasNoWarnings()
    {
        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.All(result.Data!.Checks, check =>
            Assert.Contains(check.Status, new[] { DoctorCheckStatus.Pass, DoctorCheckStatus.Info }));
        Assert.Equal(DoctorCheckStatus.Info, Check(result.Data, "profiles").Status);
        Assert.Equal(DoctorCheckStatus.Info, Check(result.Data, "authentication").Status);
        Assert.Equal(DoctorCheckStatus.Info, Check(result.Data, "update-cache").Status);
    }

    [Fact]
    public void Handle_CachedNewerVersionWarnsWithoutFailing()
    {
        new UpdateCheckStore(_dir).Save("2.0.0");

        var result = CreateHandler().Handle("1.0.0", Terminal);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2.0.0", result.Data!.LatestVersion);
        var update = Check(result.Data, "update-cache");
        Assert.Equal(DoctorCheckStatus.Warning, update.Status);
        Assert.Matches(@"^2\.0\.0 is available \(checked \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC\); run 'tx update'$", update.Message);
    }

    [Theory]
    [InlineData(InstallKind.Development)]
    [InlineData(InstallKind.Unknown)]
    public void Handle_CachedNewerVersionIsInfoWhenTxUpdateCannotApplyIt(InstallKind installKind)
    {
        new UpdateCheckStore(_dir).Save("2.0.0");

        var result = CreateHandler(installKind: installKind).Handle("1.0.0", Terminal);

        Assert.Equal(installKind, result.Data!.InstallKind);
        var update = Check(result.Data, "update-cache");
        Assert.Equal(DoctorCheckStatus.Info, update.Status);
        Assert.StartsWith("latest release is 2.0.0 (checked ", update.Message);
        Assert.DoesNotContain("run 'tx update'", update.Message);
    }

    [Fact]
    public void Handle_CachedSameVersionPasses()
    {
        new UpdateCheckStore(_dir).Save("1.0.0");

        var update = Check(CreateHandler().Handle("1.0.0", Terminal).Data!, "update-cache");

        Assert.Equal(DoctorCheckStatus.Pass, update.Status);
        Assert.StartsWith("up to date (checked ", update.Message);
    }

    [Fact]
    public void Handle_CorruptStartupConfigFailsHealthCheck()
    {
        var result = CreateHandler(configLoadError: "Config file is corrupt").Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(DoctorCheckStatus.Fail, Check(result.Data!, "configuration").Status);
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
            name => Assert.Equal(DoctorCheckStatus.Fail, Check(result.Data!, name).Status));
    }

    [Theory]
    [InlineData(false, "1 profile(s) without a usable target; run with --show-details to name them", "1 invalid session file(s); run with --show-details to name them")]
    [InlineData(true, "profile(s) without a usable target: secret-profile", "invalid session file(s): secret-session")]
    public void Handle_InvalidProfileAndSessionNamesAreShownOnlyWithDetails(bool showDetails, string profiles, string sessions)
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), """{ "secret-profile": { "Name": "secret-profile" } }""");
        Directory.CreateDirectory(Path.Combine(_dir, "sessions"));
        File.WriteAllText(Path.Combine(_dir, "sessions", "secret-session.json"), "not json");

        var result = CreateHandler().Handle("1.0.0", Terminal, showDetails).Data!;

        Assert.Equal(profiles, Check(result, "profiles").Message);
        Assert.Equal(sessions, Check(result, "sessions").Message);
    }

    [Fact]
    public void Handle_SessionsOfExitedShellsSuggestPrune()
    {
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "pid-2147483647.json"), """{ "Model": "C:/m", "Local": true }""");
        File.WriteAllText(Path.Combine(sessions, "named.json"), """{ "Model": "C:/m", "Local": true }""");

        var check = Check(CreateHandler().Handle("1.0.0", Terminal).Data!, "sessions");

        Assert.Equal(DoctorCheckStatus.Info, check.Status);
        Assert.Equal("valid (2 session(s), 1 stale; the next 'tx connect <target>' removes them)", check.Message);
    }

    [Fact]
    public void Handle_MissingConfigDirectoryIsReportedWithoutCreatingIt()
    {
        var missing = Path.Combine(_dir, "never-created");

        var result = CreateHandler(directory: missing).Handle("1.0.0", Terminal);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(DoctorCheckStatus.Info, Check(result.Data!, "config-directory").Status);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Handle_HidesHomeDirectoryUnlessDetailsAreShown()
    {
        var config = Path.Combine(_dir, ".tomix");
        Directory.CreateDirectory(config);

        var hidden = CreateHandler(directory: config, homeDirectory: _dir).Handle("1.0.0", Terminal).Data!;
        var shown = CreateHandler(directory: config, homeDirectory: _dir).Handle("1.0.0", Terminal, showDetails: true).Data!;

        var expected = Path.Combine("~", ".tomix");
        Assert.Equal(expected, hidden.ConfigDirectory);
        Assert.Equal($"read/write: {expected}", Check(hidden, "config-directory").Message);
        Assert.Equal(config, shown.ConfigDirectory);
        Assert.Equal($"read/write: {config}", Check(shown, "config-directory").Message);
    }

    [Fact]
    public void Handle_DoesNotHideADirectoryThatOnlyStartsLikeHome()
    {
        var config = Path.Combine(_dir, "home-sibling");
        Directory.CreateDirectory(config);

        var result = CreateHandler(directory: config, homeDirectory: Path.Combine(_dir, "home")).Handle("1.0.0", Terminal).Data!;

        Assert.Equal(config, result.ConfigDirectory);
    }

    [Theory]
    [InlineData("Interactive", null, true, DoctorCheckStatus.Warning)]
    [InlineData("Interactive", "tomix-msal-user.cache", true, DoctorCheckStatus.Pass)]
    [InlineData("Interactive", "tomix-msal-app.cache", true, DoctorCheckStatus.Warning)]
    [InlineData("ServicePrincipalSecret", "tomix-msal-app.cache", true, DoctorCheckStatus.Pass)]
    [InlineData("ManagedIdentity", null, true, DoctorCheckStatus.Pass)]
    [InlineData("Interactive", null, false, DoctorCheckStatus.Pass)]
    public void Handle_AuthenticationChecksTheTokenCacheForTheSignInMethod(
        string method, string? cacheFile, bool tokenCacheOnDisk, DoctorCheckStatus expected)
    {
        var auth = Path.Combine(_dir, "auth");
        Directory.CreateDirectory(auth);
        File.WriteAllText(
            Path.Combine(auth, "auth-state.json"),
            $$"""{ "Method": "{{method}}", "Username": "someone@example.com" }""");
        if (cacheFile is not null)
            File.WriteAllText(Path.Combine(auth, cacheFile), "cache");

        var handler = CreateHandler(tokenCacheOnDisk: tokenCacheOnDisk);
        var shown = Check(handler.Handle("1.0.0", Terminal, showDetails: true).Data!, "authentication");
        var hidden = Check(handler.Handle("1.0.0", Terminal).Data!, "authentication");

        Assert.Equal(expected, shown.Status);
        Assert.Equal(expected, hidden.Status);
        Assert.StartsWith($"signed in as someone@example.com ({method})", shown.Message);
        Assert.StartsWith($"signed in ({method})", hidden.Message);
        Assert.DoesNotContain("someone", hidden.Message);
    }

    [Fact]
    public void Handle_ReportsCurrentSessionTarget()
    {
        var state = new CliStateStore(_dir, currentSessionId: "doctor-test");
        state.SaveCurrentSession(new CliConnectionState("localhost:1234", "Sales", null, null, false, "dev"));

        var check = Check(CreateHandler(state: state).Handle("1.0.0", Terminal, showDetails: true).Data!, "current-session");

        Assert.Equal(DoctorCheckStatus.Pass, check.Status);
        Assert.Equal("doctor-test: server localhost:1234, database Sales (profile dev)", check.Message);
    }

    [Theory]
    [InlineData("localhost:1234", null, "local Analysis Services instance")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Secret Workspace", null, "Power BI / Fabric workspace")]
    [InlineData("asazure://westeurope.asazure.windows.net/secret", null, "Azure Analysis Services server")]
    [InlineData("secret-server", null, "Analysis Services server")]
    [InlineData(null, "C:/secret/model", "local model files")]
    public void Handle_HidesCurrentSessionNamesByDefault(string? server, string? model, string kind)
    {
        var state = new CliStateStore(_dir, currentSessionId: "secret-session");
        state.SaveCurrentSession(new CliConnectionState(server, "SecretDb", model, null, model is not null, "secret-profile"));

        var check = Check(CreateHandler(state: state).Handle("1.0.0", Terminal).Data!, "current-session");

        Assert.Equal($"named session: {kind}", check.Message);
    }

    [Fact]
    public void Handle_ReportsDisconnectedCurrentSessionAsInfo()
    {
        var check = Check(CreateHandler().Handle("1.0.0", Terminal).Data!, "current-session");

        Assert.Equal(DoctorCheckStatus.Info, check.Status);
        Assert.Equal("named session: not connected", check.Message);
    }

    [Fact]
    public void Handle_UnwritableConfigPathFails()
    {
        var path = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(path, "file");

        var result = CreateHandler(directory: path).Handle("1.0.0", Terminal);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(DoctorCheckStatus.Fail, Check(result.Data!, "config-directory").Status);
    }
}
