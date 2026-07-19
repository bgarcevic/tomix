namespace Tomix.App.Test;

/// <summary>
/// Per-test outcome. <c>Passed</c>/<c>Failed</c>/<c>Missing</c>/<c>Error</c> apply to compare
/// runs (Missing = no snapshot recorded yet); <c>Updated</c>/<c>Unchanged</c> apply to
/// <c>--update</c> runs. Serialized as strings via the shared JSON output options.
/// </summary>
public enum TestOutcome
{
    Passed,
    Failed,
    Missing,
    Error,
    Updated,
    Unchanged
}

/// <param name="Name">Test name (relative path without extension, '/'-normalized).</param>
/// <param name="File">Full path of the <c>.dax</c> file.</param>
/// <param name="Differences">First differences for a Failed test (capped); null otherwise.</param>
/// <param name="TotalDifferences">Full difference count, including those beyond the cap.</param>
public sealed record TestCaseResult(
    string Name,
    string File,
    TestOutcome Outcome,
    long DurationMs,
    string? Message = null,
    IReadOnlyList<TestDifference>? Differences = null,
    int TotalDifferences = 0);

/// <summary>
/// Result of a <c>tx test</c> run. Property names and order are the documented JSON output
/// contract (additive changes only). Non-passing tests are result data, not diagnostics —
/// the handler still returns Ok with a non-zero exit code so the full report renders.
/// </summary>
public sealed record TestRunResult(
    string Server,
    string Database,
    string Path,
    IReadOnlyList<TestCaseResult> Tests,
    int Passed,
    int Failed,
    int Missing,
    int Errored,
    int Updated,
    long DurationMs);
