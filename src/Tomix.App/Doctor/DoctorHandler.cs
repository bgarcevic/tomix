using System.Runtime.InteropServices;
using Tomix.App.Update;
using Tomix.Core.Doctor;
using Tomix.Core.Results;
using Tomix.Core.Update;

namespace Tomix.App.Doctor;

public sealed class DoctorHandler
{
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(3);

    private readonly string _configDirectory;
    private readonly IReleaseSource? _releaseSource;
    private readonly InstallKind? _installKind;

    /// <param name="configDirectory">
    /// The resolved config directory from the composition root, so doctor reports the same
    /// location every other command uses instead of re-reading the environment.
    /// </param>
    /// <param name="releaseSource">Latest-release lookup for the update check; null skips it.</param>
    /// <param name="installKind">Override for tests; defaults to inspecting the running process.</param>
    public DoctorHandler(string configDirectory, IReleaseSource? releaseSource = null, InstallKind? installKind = null)
    {
        _configDirectory = configDirectory;
        _releaseSource = releaseSource;
        _installKind = installKind;
    }

    public TomixResult<DoctorResult> Handle(string version)
    {
        var checks = new List<DoctorCheck>();

        checks.Add(new DoctorCheck(
            Name: "runtime",
            Status: DoctorCheckStatus.Pass,
            Message: $".NET {Environment.Version}"));

        checks.Add(new DoctorCheck(
            Name: "operating-system",
            Status: DoctorCheckStatus.Pass,
            Message: RuntimeInformation.OSDescription));

        var configDirectory = _configDirectory;

        try
        {
            Directory.CreateDirectory(configDirectory);

            checks.Add(new DoctorCheck(
                Name: "config-directory",
                Status: DoctorCheckStatus.Pass,
                Message: configDirectory));
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck(
                Name: "config-directory",
                Status: DoctorCheckStatus.Fail,
                Message: $"Could not create config directory: {ex.Message}"));
        }

        var latestVersion = AddUpdateCheck(checks, version);

        var result = new DoctorResult(
            Version: version,
            OperatingSystem: RuntimeInformation.OSDescription,
            DotNetVersion: Environment.Version.ToString(),
            ConfigDirectory: configDirectory,
            Checks: checks,
            LatestVersion: latestVersion);

        var hasFailure = checks.Any(check => check.Status == DoctorCheckStatus.Fail);

        return new TomixResult<DoctorResult>(
            Success: !hasFailure,
            Data: result,
            Diagnostics: Array.Empty<Tomix.Core.Diagnostics.TomixDiagnostic>(),
            ExitCode: hasFailure ? 1 : 0);
    }

    /// <summary>
    /// Compares the installed version against the latest GitHub release. Doctor is a
    /// diagnostic command, so a short (3s) live lookup is acceptable here; the result is
    /// never a Fail — network state must not change doctor's exit code.
    /// </summary>
    private string? AddUpdateCheck(List<DoctorCheck> checks, string version)
    {
        if (_releaseSource is null)
            return null;

        var installKind = _installKind ?? InstallationInspector.Detect();
        if (installKind == InstallKind.Development)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(UpdateCheckTimeout);
            var latest = _releaseSource.GetLatestAsync(cts.Token).GetAwaiter().GetResult();

            if (latest is null
                || !CliVersion.TryParse(latest.Version, out var latestVersion)
                || !CliVersion.TryParse(version, out var currentVersion))
            {
                checks.Add(new DoctorCheck(
                    Name: "update",
                    Status: DoctorCheckStatus.Warning,
                    Message: "could not determine the latest released version"));
                return null;
            }

            checks.Add(latestVersion.IsNewerThan(currentVersion)
                ? new DoctorCheck(
                    Name: "update",
                    Status: DoctorCheckStatus.Warning,
                    Message: $"update available: {latest.Version} (run 'tx update')")
                : new DoctorCheck(
                    Name: "update",
                    Status: DoctorCheckStatus.Pass,
                    Message: $"up to date ({version})"));

            return latest.Version;
        }
        catch (Exception)
        {
            checks.Add(new DoctorCheck(
                Name: "update",
                Status: DoctorCheckStatus.Warning,
                Message: "could not check for updates (github.com unreachable?)"));
            return null;
        }
    }
}
