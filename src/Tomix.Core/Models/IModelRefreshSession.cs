using System.Text.Json.Serialization;

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

/// <param name="Phases">Model-level processing phases after (and around) data load, from the
/// session trace; null when no trace was captured.</param>
public sealed record ModelRefreshResult(
    string Server,
    string Database,
    string RefreshType,
    long DurationMs,
    IReadOnlyList<RefreshTableResult> Tables,
    RefreshTableResult? Totals,
    IReadOnlyList<RefreshPhaseResult>? Phases = null);

/// <summary>
/// Per-table rollup. <see cref="QueryMs"/>, <see cref="ReadMs"/>, and <see cref="TotalMs"/>
/// are populated from XMLA ProgressReport events when available; otherwise 0. For a table with
/// several partitions they are sums across its partitions (which may have run in parallel).
/// </summary>
/// <param name="ProcessMs">Post-load processing attributed to the table: attribute and user
/// hierarchies and calculated columns (summed).</param>
/// <param name="Partitions">Per-partition breakdown from the session trace; null when no trace
/// was captured.</param>
public sealed record RefreshTableResult(
    string Table,
    long Rows,
    long QueryMs,
    long ReadMs,
    long TotalMs,
    long ProcessMs = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RefreshPartitionResult>? Partitions = null);

/// <summary>One partition's load: source query, data read, and their sum.</summary>
public sealed record RefreshPartitionResult(
    string Partition,
    long Rows,
    long QueryMs,
    long ReadMs,
    long TotalMs);

/// <summary>
/// A model-level refresh phase (for example <c>relationships</c> or <c>commit</c>).
/// <see cref="DurationMs"/> is wall-clock: overlapping events (parallel tables) are counted once.
/// </summary>
/// <param name="Phase">Stable lowercase name: <c>load</c>, <c>hierarchies</c>,
/// <c>calculatedColumns</c>, <c>relationships</c>, <c>calculationScript</c>,
/// <c>sequencePoint</c>, or <c>commit</c>.</param>
/// <param name="Count">Number of objects processed in the phase.</param>
public sealed record RefreshPhaseResult(string Phase, int Count, long DurationMs);

/// <summary>
/// Progress snapshot reported during refresh, surfaced from XMLA SessionTrace events.
/// <see cref="Table"/> is null for model-level events. <see cref="RowsRead"/> is the running
/// total for the indicated table. <see cref="Completed"/> marks the per-table end event.
/// </summary>
/// <param name="Partition">The partition the event belongs to, when the table has more than one.</param>
public sealed record RefreshProgress(
    string? Table,
    long? RowsRead,
    string? Phase,
    bool Completed,
    string? Partition = null);
