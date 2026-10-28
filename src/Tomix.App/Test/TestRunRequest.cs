namespace Tomix.App.Test;

/// <param name="Path">Test file or directory (recursive <c>*.dax</c> discovery).</param>
/// <param name="Update">Record mode: (re)write each <c>.expected.json</c> from actual results instead of comparing.</param>
/// <param name="UpdateMissing">Record only tests that have no snapshot yet; compare the rest. Excludes <paramref name="Update"/>.</param>
/// <param name="Filter">Optional <c>*</c>/<c>?</c> wildcard on test names.</param>
/// <param name="Parameters">Query parameters applied to every test, referenced as <c>@name</c> in DAX.</param>
/// <param name="MaxRows">Per-query row cap; a query exceeding it is a per-test error, not a truncated comparison.</param>
/// <param name="Compare">Run-wide comparison defaults; a test's own <c>tx:</c> directives override them.</param>
/// <param name="BaselineServer">Baseline mode: run every test on this model too and compare with its result
/// instead of a snapshot. Defaults to the primary target's server when only <paramref name="BaselineDatabase"/> is given.</param>
/// <param name="BaselineDatabase">The baseline model's database; defaults to the primary target's database.</param>
/// <param name="BaselineProfile">The saved profile the baseline target came from, reported in the result.</param>
/// <param name="Tags">Run only tests with any of these <c>tags=</c> (case-insensitive); null or empty runs every test.</param>
public sealed record TestRunRequest(
    string? Model,
    string? Server,
    string? Database,
    string? Auth,
    string Path,
    bool Update = false,
    string? Filter = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    int MaxRows = 10000,
    TestCompareOptions? Compare = null,
    bool UpdateMissing = false,
    string? BaselineServer = null,
    string? BaselineDatabase = null,
    string? BaselineProfile = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>True when tests compare with a second live model instead of their snapshots.</summary>
    public bool HasBaseline
        => !string.IsNullOrWhiteSpace(BaselineServer) || !string.IsNullOrWhiteSpace(BaselineDatabase);
}
