namespace Tomix.App.Update;

/// <summary>Runs an external process to completion. Seam so the dotnet-tool update path is testable.</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);
