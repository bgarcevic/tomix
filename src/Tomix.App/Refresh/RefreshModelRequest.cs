using Tomix.Core.Models;

namespace Tomix.App.Refresh;

/// <param name="Model">Raw --model value (local path or remote endpoint). Null = use active session.</param>
/// <param name="Server">Explicit -s/--server override.</param>
/// <param name="Database">Explicit -d/--database override.</param>
/// <param name="Auth">--auth hint (auto/interactive/spn/env/managed-identity); resolved by the provider via token.</param>
/// <param name="RefreshType">full, dataonly, automatic, calculate, clearvalues, defragment, add.</param>
/// <param name="Tables">--table (repeatable). Null/empty = whole model.</param>
/// <param name="Partitions">--partition (repeatable, Table.Partition). Requires <see cref="Tables"/> null/empty.</param>
/// <param name="ApplyRefreshPolicy">--apply-refresh-policy (default true).</param>
/// <param name="EffectiveDate">--effective-date (yyyy-MM-dd).</param>
/// <param name="MaxParallelism">--max-parallelism.</param>
/// <param name="DryRun">--dry-run: emit TMSL without executing.</param>
/// <param name="NoProgress">--no-progress: suppress live progress reporting.</param>
/// <param name="TracePath">--trace: null=off, "-"=stderr, path=write to file.</param>
public sealed record RefreshModelRequest(
    string? Model,
    string? Server,
    string? Database,
    string? Auth,
    string RefreshType,
    IReadOnlyList<string>? Tables,
    IReadOnlyList<TablePartition>? Partitions,
    bool ApplyRefreshPolicy,
    DateOnly? EffectiveDate,
    int? MaxParallelism,
    bool DryRun,
    bool NoProgress,
    string? TracePath);
