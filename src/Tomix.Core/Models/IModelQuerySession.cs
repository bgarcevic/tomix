namespace Tomix.Core.Models;

/// <summary>
/// Session capability that executes a DAX (<c>EVALUATE</c>) or DMV (<c>SELECT ... FROM $SYSTEM....</c>)
/// query against a live model and returns the rowset. Mirrors <see cref="IModelRefreshSession"/>:
/// only live server sessions implement it; file/folder sessions cannot evaluate queries.
/// </summary>
public interface IModelQuerySession
{
    /// <param name="traceWriter">Optional sink for raw XMLA trace events (from <c>--trace &lt;path&gt;</c>);
    /// null disables the raw dump. Server-timings/plan capture is driven by <see cref="ModelQueryRequest"/>.</param>
    Task<ModelQueryResult> ExecuteQueryAsync(
        ModelQueryRequest request,
        TextWriter? traceWriter,
        CancellationToken cancellationToken);
}

/// <param name="Query">The query text, sent to the server as-is.</param>
/// <param name="Parameters">Optional named parameters referenced as <c>@name</c> in DAX. Values are passed as strings.</param>
/// <param name="MaxRows">Client-side row cap. Implementations read one extra row to detect truncation.</param>
/// <param name="Trace">Capture server timings (formula- vs storage-engine) via an XMLA trace. Requires admin
/// rights on the endpoint; degrades best-effort (rowset still returned) when tracing is unavailable.</param>
/// <param name="Plan">Capture the logical and physical DAX query plans. Implies a trace.</param>
/// <param name="ClearCache">Clear the model cache (and warm it up) before each run so timings reflect a cold cache.</param>
/// <param name="Runs">Number of times to execute the query (>= 1). Values &gt; 1 yield per-run timings for benchmarking.</param>
public sealed record ModelQueryRequest(
    string Query,
    IReadOnlyDictionary<string, string>? Parameters = null,
    int? MaxRows = null,
    bool Trace = false,
    bool Plan = false,
    bool ClearCache = false,
    int Runs = 1);

/// <param name="Name">Column name exactly as returned by the server (e.g. <c>Sales[Amount]</c> or <c>[Total Sales]</c>).</param>
/// <param name="Type">Normalized type name: string, int64, double, decimal, boolean, dateTime, or object.</param>
public sealed record QueryColumn(string Name, string Type);

/// <summary>
/// Server-measured timings for one query execution, aggregated from XMLA trace events.
/// <see cref="FormulaEngineMs"/> is <c>max(Total − StorageEngine, 0)</c> (a first-order split;
/// it does not model overlapping parallel storage-engine scans).
/// </summary>
public sealed record QueryTimings(
    long TotalMs,
    long TotalCpuMs,
    long FormulaEngineMs,
    long StorageEngineMs,
    long StorageEngineCpuMs,
    int StorageEngineQueryCount,
    int StorageEngineCacheHits);

/// <summary>A DAX query plan captured from a <c>DAXQueryPlan</c> trace event.</summary>
/// <param name="Kind">Either <c>"logical"</c> or <c>"physical"</c>.</param>
/// <param name="Text">The verbatim, tab-indented plan text.</param>
public sealed record QueryPlan(string Kind, string Text);

/// <summary>
/// One execution within a (possibly multi-run) query. <see cref="ClientMs"/> is wall-clock as
/// measured by the client; <see cref="Timings"/> is the server-measured breakdown, present only
/// when a trace was active for the run.
/// </summary>
public sealed record QueryRun(int Index, bool Cold, long ClientMs, QueryTimings? Timings);

/// <summary>
/// Query rowset. Cell values are restricted to <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/>,
/// or null (DAX BLANK); implementations must map anything else to a string.
/// <see cref="Runs"/> and <see cref="Plans"/> are null unless the corresponding perf option was
/// requested and honored (append-only additions preserve the layering contract).
/// </summary>
public sealed record ModelQueryResult(
    string Server,
    string Database,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    long DurationMs,
    IReadOnlyList<QueryRun>? Runs = null,
    IReadOnlyList<QueryPlan>? Plans = null);
