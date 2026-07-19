namespace Tomix.App.Test;

/// <param name="Path">Test file or directory (recursive <c>*.dax</c> discovery).</param>
/// <param name="Update">Record mode: (re)write each <c>.expected.json</c> from actual results instead of comparing.</param>
/// <param name="Filter">Optional <c>*</c>/<c>?</c> wildcard on test names.</param>
/// <param name="Parameters">Query parameters applied to every test, referenced as <c>@name</c> in DAX.</param>
/// <param name="MaxRows">Per-query row cap; a query exceeding it is a per-test error, not a truncated comparison.</param>
public sealed record TestRunRequest(
    string? Model,
    string? Server,
    string? Database,
    string? Auth,
    string Path,
    bool Update = false,
    string? Filter = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    int MaxRows = 10000);
