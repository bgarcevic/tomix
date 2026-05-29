namespace Mdl.Core.Doctor;

public sealed record DoctorResult(
    string Version,
    string OperatingSystem,
    string DotNetVersion,
    string ConfigDirectory,
    IReadOnlyList<DoctorCheck> Checks);