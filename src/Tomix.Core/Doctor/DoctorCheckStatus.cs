namespace Tomix.Core.Doctor;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Fail,
    /// <summary>A normal, optional state worth reporting (for example "no profiles configured").</summary>
    Info
}
