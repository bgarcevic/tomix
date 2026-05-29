using Mdl.Core.Diagnostics;

namespace Mdl.Core.Results;

public sealed record MdlResult<T>(
    bool Success,
    T? Data,
    IReadOnlyList<MdlDiagnostic> Diagnostics,
    int ExitCode)
{
    public static MdlResult<T> Ok(T data)
    {
        return new MdlResult<T>(
            Success: true,
            Data: data,
            Diagnostics: Array.Empty<MdlDiagnostic>(),
            ExitCode: 0);
    }

    public static MdlResult<T> Fail(
        string code,
        string message,
        int exitCode = 1)
    {
        return new MdlResult<T>(
            Success: false,
            Data: default,
            Diagnostics:
            [
                new MdlDiagnostic(
                    Code: code,
                    Severity: DiagnosticSeverity.Error,
                    Message: message)
            ],
            ExitCode: exitCode);
    }
}