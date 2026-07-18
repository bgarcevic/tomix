using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using AsTraceEventClass = Microsoft.AnalysisServices.TraceEventClass;
using AsTraceEventSubclass = Microsoft.AnalysisServices.TraceEventSubclass;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;
using TabularTrace = Microsoft.AnalysisServices.Tabular.Trace;
using TabularTraceEventArgs = Microsoft.AnalysisServices.Tabular.TraceEventArgs;
using TabularTraceEventHandler = Microsoft.AnalysisServices.Tabular.TraceEventHandler;

namespace Tomix.Provider.Tom;

/// <summary>
/// Subscribes to <see cref="TabularServer.SessionTrace"/> for the duration of a refresh,
/// parses ProgressReport events into per-table accumulators, and forwards live snapshots
/// to an <see cref="IProgress{RefreshProgress}"/>. Also mirrors raw events to a
/// <see cref="TextWriter"/> when --trace is set.
/// </summary>
internal sealed class RefreshTraceSink : IDisposable
{
    private readonly TabularServer? _server;
    private readonly IReadOnlyList<string> _knownTables;
    private readonly IProgress<RefreshProgress>? _progress;
    private readonly TextWriter? _traceWriter;
    private readonly ConcurrentDictionary<string, TableAccumulator> _tables = new(StringComparer.Ordinal);
    // Per-table error messages captured from ProgressReportError events. Keyed by resolved table
    // name; values are the error texts (typically the engine's localized message). When the table
    // can't be resolved, the special key "" holds the unmatched errors.
    private readonly ConcurrentDictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private TabularTraceEventHandler? _handler;

    private RefreshTraceSink(TabularServer? server, IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress, TextWriter? traceWriter)
    {
        _server = server;
        _knownTables = knownTables;
        _progress = progress;
        _traceWriter = traceWriter;
    }

    /// <summary>
    /// Test-only constructor: builds a sink without an AMO server so unit tests can drive
    /// <see cref="HandleProgress(RefreshTraceEvent)"/> with synthetic events. The session trace
    /// is never attached in this mode.
    /// </summary>
    internal RefreshTraceSink(IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress = null, TextWriter? traceWriter = null)
        : this(server: null, knownTables, progress, traceWriter)
    {
    }

    /// <summary>Attaches a session trace to <paramref name="server"/> and returns a sink whose
    /// <see cref="BuildTableResults"/> yields per-table rollups after the refresh completes.
    /// <paramref name="knownTables"/> is the set of tables being refreshed, used to map child-object
    /// events (hierarchies, columns, relationships) back to their parent table.</summary>
    public static RefreshTraceSink? Attach(TabularServer server, IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress, TextWriter? traceWriter)
    {
        if (progress is null && traceWriter is null)
            return null;

        var sink = new RefreshTraceSink(server, knownTables, progress, traceWriter);
        sink.AttachToSessionTrace();
        return sink;
    }

    /// <summary>
    /// Attaches a summary-only sink: captures per-table durations for the final summary even when
    /// there's no live progress channel or trace writer. Use this as a fallback so JSON/CSV/piped
    /// output still gets real per-table numbers.
    /// </summary>
    public static RefreshTraceSink AttachSummaryOnly(TabularServer server, IReadOnlyList<string> knownTables)
    {
        var sink = new RefreshTraceSink(server, knownTables, progress: null, traceWriter: null);
        sink.AttachToSessionTrace();
        return sink;
    }

    private void AttachToSessionTrace()
    {
        if (_server is null) return;
        try
        {
            // server.SessionTrace is a pre-configured session-scoped trace that already
            // captures ProgressReport/Error/Command events for our session. We just subscribe.
            var trace = _server.SessionTrace;
            _handler = OnEvent;
            trace.OnEvent += _handler;
            trace.Start();
        }
        catch (Exception ex)
        {
            // The session trace is best-effort: if the server doesn't allow it, the refresh
            // still runs; we just lose live progress and per-table stats. The handler stays null
            // so the caller's per-table fallback takes over. Surface the failure on stderr so the
            // user can debug (e.g. permission issue, server restriction) via the trace log.
            try { Console.Error.WriteLine($"[tomix] session trace unavailable: {ex.Message}"); } catch { }
            _handler = null;
        }
    }

    private void OnEvent(object? sender, TabularTraceEventArgs e)
    {
        try
        {
            // Project the AMO event into our testfriendly record, then forward. The projection
            // is the only place that touches TabularTraceEventArgs; downstream code (WriteTrace,
            // HandleProgress, ResolveTableName, CaptureError) is AMO-free and unit-testable.
            var ev = new RefreshTraceEvent(
                e.StartTime,
                e.EventClass,
                e.EventSubclass,
                e.Duration,
                e.IntegerData,
                e.ObjectName,
                e.TextData,
                e.Error);
            Process(ev);
        }
        catch
        {
            // Trace handler must never throw into the AMO event loop.
        }
    }

    /// <summary>
    /// Single entry point for an already-projected event: traces it (when --trace is set) then
    /// routes it through <see cref="HandleProgress"/>. Production code reaches this via
    /// <see cref="OnEvent"/>; tests call it directly with synthetic events so a single call
    /// exercises both the trace dump and the accumulator routing.
    /// </summary>
    internal void Process(RefreshTraceEvent e)
    {
        WriteTrace(e);
        HandleProgress(e);
    }

    private void WriteTrace(RefreshTraceEvent e)
    {
        if (_traceWriter is null) return;
        var line = new StringBuilder()
            .Append(e.StartTime.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
            .Append(e.EventClass).Append('/').Append(e.EventSubclass).Append('\t')
            .Append("dur=").Append(e.Duration).Append('\t')
            .Append("int=").Append(e.IntegerData).Append('\t')
            .Append("obj=").Append(e.ObjectName);
        if (!string.IsNullOrEmpty(e.TextData))
            line.Append('\t').Append(e.TextData.Replace('\n', ' ').Replace('\r', ' ').Trim());
        if (!string.IsNullOrEmpty(e.Error))
            line.Append('\t').Append("error=").Append(e.Error.Replace('\n', ' ').Trim());
        _traceWriter.WriteLine(line.ToString());
    }

    /// <summary>
    /// Routes a refresh trace event into the per-table accumulators. Power BI Service emits only
    /// <c>ProgressReportEnd</c> events during refresh (verified against AMO 19.114 trace dumps);
    /// <c>ProgressReportCurrent</c> is rare there but common on on-prem AS, so we keep handling
    /// it defensively for live row-count updates.
    /// </summary>
    internal void HandleProgress(RefreshTraceEvent e)
    {
        // Always process events: even when _progress is null (summary-only mode), we need to
        // populate _tables for the final per-table summary.
        if (e.EventClass is not (AsTraceEventClass.ProgressReportBegin
            or AsTraceEventClass.ProgressReportCurrent
            or AsTraceEventClass.ProgressReportEnd
            or AsTraceEventClass.ProgressReportError))
            return;

        // Capture per-table errors before any subclass filtering: ProgressReportError events
        // carry the failing object/table context that server.Execute's XmlaError messages lack.
        // The error text is preferred over TextData (the former is the localized message).
        if (e.EventClass == AsTraceEventClass.ProgressReportError)
        {
            CaptureError(e);
            return;
        }

        // Only End events carry useful durations; Current events may carry live row counts.
        var isEnd = e.EventClass == AsTraceEventClass.ProgressReportEnd;
        var isCurrent = e.EventClass == AsTraceEventClass.ProgressReportCurrent;

        // Commit marks the overall refresh as done — mark every tracked table completed.
        if (isEnd && e.EventSubclass == AsTraceEventSubclass.TabularCommit)
        {
            foreach (var entry in _tables.Values)
                entry.Completed = true;
            _progress?.Report(new RefreshProgress(Table: "", RowsRead: null, Phase: "commit", Completed: true));
            return;
        }

        var subclass = e.EventSubclass;
        var isPhaseEvent = subclass == AsTraceEventSubclass.ExecuteSql
            || subclass == AsTraceEventSubclass.ReadData
            || subclass == AsTraceEventSubclass.TabularRefresh;
        if (!isPhaseEvent)
            return;

        var table = ResolveTableName(e);
        if (string.IsNullOrEmpty(table))
        {
            // Some phase events (e.g. CompressSegment for a column) resolve to a child name
            // that's not in _knownTables; only attach to a single active table to avoid noise.
            table = FindActiveTable();
            if (string.IsNullOrEmpty(table))
                return;
        }

        var acc = _tables.GetOrAdd(table, _ => new TableAccumulator(table));

        if (isEnd)
        {
            switch (subclass)
            {
                case AsTraceEventSubclass.ExecuteSql:
                    // Source query (SQL or M) end: this is the "Query" column. Carries no row count.
                    acc.QueryMs = e.Duration;
                    break;
                case AsTraceEventSubclass.ReadData:
                    // Data read end: this is the "Read" column. IntegerData is the authoritative
                    // row count for the table (replaces the previous post-refresh DMV query).
                    acc.ReadMs = e.Duration;
                    if (e.IntegerData > 0) acc.Rows = e.IntegerData;
                    break;
                case AsTraceEventSubclass.TabularRefresh:
                    // Partition-level refresh end. ObjectName is the partition name, which equals
                    // the table name for single-partition tables. This duration is the total
                    // processing time INCLUDING commit/lock overhead; we keep it as a fallback
                    // for tables where neither ExecuteSql nor ReadData fired (defensive — never
                    // observed in real traces). The previously-buggy fallback that set
                    // QueryMs = TotalMs is gone.
                    if (string.Equals(e.ObjectName, table, StringComparison.Ordinal))
                    {
                        acc.TabularRefreshMs = e.Duration;
                        acc.Completed = true;
                    }
                    break;
            }
        }
        else if (isCurrent && e.IntegerData > 0)
        {
            // Live row count during SQL execution. Power BI Service does not emit these, but
            // on-prem AS / Fabric may; capture defensively so the spinner ticks during long reads.
            acc.Rows = e.IntegerData;
        }

        if (_progress is not null)
        {
            var phase = subclass switch
            {
                AsTraceEventSubclass.ExecuteSql => "query",
                AsTraceEventSubclass.ReadData => "read",
                AsTraceEventSubclass.TabularRefresh => acc.Completed ? "done" : "processing",
                _ => "processing"
            };
            _progress.Report(new RefreshProgress(
                Table: table,
                RowsRead: acc.Rows > 0 ? acc.Rows : null,
                Phase: phase,
                Completed: acc.Completed));
        }
    }

    /// <summary>
    /// Resolves the table name for an event. <see cref="RefreshTraceEvent.ObjectName"/> is the
    /// object being processed — for table-level events it IS the table; for child-object events
    /// (hierarchies, columns, relationships) it's the child name, so we match against the known
    /// table list by checking TextData (which embeds the table name in localized descriptions).
    /// </summary>
    private string ResolveTableName(RefreshTraceEvent e)
    {
        // Fast path: ObjectName is a known table.
        if (!string.IsNullOrWhiteSpace(e.ObjectName))
        {
            foreach (var known in _knownTables)
            {
                if (string.Equals(e.ObjectName, known, StringComparison.Ordinal))
                    return known;
            }
        }

        // Fallback: scan TextData for any known table name. Events carry localized descriptions
        // like "Processing of hierarchy 'X' in table 'Datoer' completed (TableTMID='13')" which
        // embed the table name. Matching the longest known table name first avoids partial matches.
        var text = e.TextData;
        if (string.IsNullOrEmpty(text))
            return "";

        var match = _knownTables
            .Where(t => text.Contains(t, StringComparison.Ordinal))
            .OrderByDescending(t => t.Length)
            .FirstOrDefault();
        return match ?? "";
    }

    private string FindActiveTable()
    {
        var candidates = _tables.Values
            .Where(a => !a.Completed)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0].Name : "";
    }

    public IReadOnlyList<RefreshTableResult> BuildTableResults()
    {
        return _tables.Values
            .Select(a => a.ToResult())
            .OrderBy(t => t.TotalMs > 0 ? t.TotalMs : long.MaxValue)
            .ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns per-table error messages captured from <c>ProgressReportError</c> events, plus any
    /// errors whose table couldn't be resolved under the empty-string key. Used by
    /// <c>TomModelRefresher</c> to enrich the failure message with the failing table name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> BuildTableErrors()
    {
        return _errors.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.Ordinal);
    }

    private void CaptureError(RefreshTraceEvent e)
    {
        var msg = e.Error;
        if (string.IsNullOrWhiteSpace(msg))
            msg = e.TextData;
        if (string.IsNullOrWhiteSpace(msg))
            return;

        var table = ResolveTableName(e);
        // An empty key means we couldn't associate the error with a known table. The caller
        // surfaces these separately so they still appear in the failure message.
        var key = string.IsNullOrEmpty(table) ? "" : table;
        var list = _errors.GetOrAdd(key, _ => new List<string>());
        lock (list)
        {
            // Dedup: the same error can fire for each partition of a failing table.
            if (!list.Contains(msg, StringComparer.Ordinal))
                list.Add(msg);
        }
    }

    public void Dispose()
    {
        if (_handler is null || _server is null) return;
        try
        {
            var trace = _server.SessionTrace;
            if (trace.IsStarted) trace.Stop();
            trace.OnEvent -= _handler;
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class TableAccumulator
    {
        public TableAccumulator(string name) => Name = name;
        public string Name { get; }
        public long Rows;
        public long QueryMs;
        public long ReadMs;
        // TabularRefresh partition-end duration. Used as a Total fallback when neither ExecuteSql
        // nor ReadData fired (defensive — never observed in real traces).
        public long TabularRefreshMs;
        public bool Completed;

        /// <summary>
        /// Total = Query + Read (matches the reference CLI's arithmetic for the majority of
        /// tables). Falls back to TabularRefreshMs when neither phase event fired.
        /// </summary>
        public RefreshTableResult ToResult()
        {
            var total = (QueryMs > 0 || ReadMs > 0)
                ? QueryMs + ReadMs
                : TabularRefreshMs;
            return new RefreshTableResult(Name, Rows, QueryMs, ReadMs, total);
        }
    }
}

/// <summary>
/// AMO-free projection of <see cref="TabularTraceEventArgs"/>. Introduced so the sink's
/// event-handling logic can be unit-tested with synthetic events instead of a live server.
/// </summary>
internal sealed record RefreshTraceEvent(
    DateTime StartTime,
    AsTraceEventClass EventClass,
    AsTraceEventSubclass EventSubclass,
    long Duration,
    long IntegerData,
    string ObjectName,
    string? TextData,
    string? Error);
