using System.Runtime.InteropServices;
using Mdl.Core.Configuration;
using Mdl.Core.Doctor;
using Mdl.Core.Results;

namespace Mdl.App.Doctor;

public sealed class DoctorHandler
{
    public MdlResult<DoctorResult> Handle(string version)
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

        var configDirectory = MdlPaths.ConfigDirectory;

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

        return new MdlResult<DoctorResult>(
            Success: !hasFailure,
            Data: result,
            Diagnostics: Array.Empty<Mdl.Core.Diagnostics.MdlDiagnostic>(),
            ExitCode: hasFailure ? 1 : 0);
    }
}