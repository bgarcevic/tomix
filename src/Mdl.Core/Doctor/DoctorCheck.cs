namespace Mdl.Core.Doctor;

public sealed record DoctorCheck(
    string Name,
    DoctorCheckStatus Status,
    string Message);