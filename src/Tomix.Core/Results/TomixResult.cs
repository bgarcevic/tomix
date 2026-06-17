using Tomix.Core.Diagnostics;

namespace Tomix.Core.Results;

public sealed record TomixResult<T>(
    bool Success,
    T? Data,
    IReadOnlyList<TomixDiagnostic> Diagnostics,
    int ExitCode)
{
    public static TomixResult<T> Ok(T data, int exitCode = 0)
    {
        return new TomixResult<T>(
            Success: true,
            Data: data,
            Diagnostics: Array.Empty<TomixDiagnostic>(),
            ExitCode: exitCode);
    }

    public static TomixResult<T> Fail(
        string code,
        string message,
        int exitCode = 1,
        string? hint = null)
    {
        return new TomixResult<T>(
            Success: false,
            Data: default,
            Diagnostics:
            [
                new TomixDiagnostic(
                    Code: code,
                    Severity: DiagnosticSeverity.Error,
                    Message: message,
                    Hint: hint)
            ],
            ExitCode: exitCode);
    }
}
