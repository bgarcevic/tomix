namespace Tomix.Core.Models;

/// <summary>
/// Session capability that triggers a data refresh on a deployed model.
/// Mirrors <see cref="IModelDeploySession"/>: implementations produce refresh TMSL via
/// <see cref="GenerateRefreshScript"/> and execute it via <see cref="RefreshAsync"/>,
/// optionally streaming progress through <paramref name="progress"/> and raw trace events
/// through <paramref name="traceWriter"/>.
/// </summary>
public interface IModelRefreshSession
{
    Task<ModelRefreshResult> RefreshAsync(
        ModelRefreshRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken);

    string GenerateRefreshScript(ModelRefreshRequest request);

    /// <summary>
    /// Applies a table's incremental refresh policy on the server: diffs the expected partition
    /// scheme for the effective date against the existing partitions and creates/merges/drops
    /// as needed. Request.Refresh=false bootstraps partition definitions without loading data.
    /// </summary>
    Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(
        RefreshPolicyApplyRequest request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Applying refresh policies requires an XMLA session.");
}

/// <param name="Database">Dataset/catalog name. When null, the session's already-resolved database is used.</param>
/// <param name="RefreshType">One of: automatic, full, dataOnly, calculate, clearValues, defragment, add.</param>
/// <param name="Tables">Optional table names to scope the refresh. Null or empty = entire model.</param>
/// <param name="Partitions">Optional partition scope (requires <see cref="Tables"/> to be null/empty).</param>
/// <param name="ApplyRefreshPolicy">When true (default), apply incremental refresh policies.</param>
/// <param name="EffectiveDate">Override the current date used for incremental refresh policy evaluation.</param>
/// <param name="MaxParallelism">When set, emits a <c>maxParallelism</c> property on the refresh command.</param>
public sealed record ModelRefreshRequest(
    string? Database,
    string RefreshType,
    IReadOnlyList<string>? Tables,
    IReadOnlyList<TablePartition>? Partitions,
    bool ApplyRefreshPolicy = true,
    DateOnly? EffectiveDate = null,
    int? MaxParallelism = null);

public sealed record TablePartition(string Table, string Partition);

public sealed record ModelRefreshResult(
    string Server,
    string Database,
    string RefreshType,
    long DurationMs,
    IReadOnlyList<RefreshTableResult> Tables,
    RefreshTableResult? Totals);

/// <summary>
/// Per-table rollup. <see cref="QueryMs"/>, <see cref="ReadMs"/>, and <see cref="TotalMs"/>
/// are populated from XMLA ProgressReport events when available; otherwise 0.
/// </summary>
public sealed record RefreshTableResult(
    string Table,
    long Rows,
    long QueryMs,
    long ReadMs,
    long TotalMs);

/// <summary>
/// Progress snapshot reported during refresh, surfaced from XMLA SessionTrace events.
/// <see cref="Table"/> is null for model-level events. <see cref="RowsRead"/> is the running
/// total for the indicated table. <see cref="Completed"/> marks the per-table end event.
/// </summary>
public sealed record RefreshProgress(
    string? Table,
    long? RowsRead,
    string? Phase,
    bool Completed);
