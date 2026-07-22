namespace Tomix.Core.Doctor;

public sealed record DoctorResult(
    string Version,
    string OperatingSystem,
    string DotNetVersion,
    string ConfigDirectory,
    DoctorTerminalCapabilities Terminal,
    IReadOnlyList<DoctorCheck> Checks,
    string? LatestVersion = null);

public sealed record DoctorTerminalCapabilities(
    bool Interactive,
    bool Ansi,
    string ColorSystem);
