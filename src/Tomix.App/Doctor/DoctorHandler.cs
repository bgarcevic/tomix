using System.Runtime.InteropServices;
using Tomix.Core.Configuration;
using Tomix.Core.Doctor;
using Tomix.Core.Results;

namespace Tomix.App.Doctor;

public sealed class DoctorHandler
{
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

        var configDirectory = TomixPaths.ConfigDirectory;

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

        var result = new DoctorResult(
            Version: version,
            OperatingSystem: RuntimeInformation.OSDescription,
            DotNetVersion: Environment.Version.ToString(),
            ConfigDirectory: configDirectory,
            Checks: checks);

        var hasFailure = checks.Any(check => check.Status == DoctorCheckStatus.Fail);

        return new TomixResult<DoctorResult>(
            Success: !hasFailure,
            Data: result,
            Diagnostics: Array.Empty<Tomix.Core.Diagnostics.TomixDiagnostic>(),
            ExitCode: hasFailure ? 1 : 0);
    }
}