namespace Tomix.App.IncrementalRefresh;

/// <param name="Model">Raw --model value (local path or remote endpoint). Null = use active session.</param>
/// <param name="Server">Explicit -s/--server override.</param>
/// <param name="Database">Explicit -d/--database override.</param>
/// <param name="EffectiveDate">--effective-date (yyyy-MM-dd); null = today.</param>
/// <param name="Refresh">False = --no-refresh bootstrap: create partition definitions without loading data.</param>
public sealed record ApplyRefreshPolicyRequest(
    string? Model,
    string? Server,
    string? Database,
    string Table,
    DateOnly? EffectiveDate,
    bool Refresh,
    int? MaxParallelism);
