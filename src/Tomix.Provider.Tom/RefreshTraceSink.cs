using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices;
using Tomix.Core.Models;
using AsTraceEventClass = Microsoft.AnalysisServices.TraceEventClass;
using AsTraceEventSubclass = Microsoft.AnalysisServices.TraceEventSubclass;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;
using TabularTraceEventArgs = Microsoft.AnalysisServices.Tabular.TraceEventArgs;
using TabularTraceEventHandler = Microsoft.AnalysisServices.Tabular.TraceEventHandler;

namespace Tomix.Provider.Tom;

/// <summary>
/// Subscribes to <see cref="TabularServer.SessionTrace"/> for the duration of a refresh and turns
/// its ProgressReport events into per-table and per-partition load statistics, per-table post-load
/// processing, and model-level phases (relationships, calculation script, commit), forwarding
/// live snapshots to an <see cref="IProgress{RefreshProgress}"/>. Also mirrors raw events to a
/// <see cref="TextWriter"/> when --trace is set.
/// <para>
/// Events are attributed by <c>ObjectPath</c> (<c>&lt;db&gt;.Model.&lt;Table&gt;.&lt;Child&gt;</c>)
/// and <c>ObjectType</c> when the server supplies them, as Power BI / Fabric does; otherwise by
/// <c>ObjectName</c> and the table name embedded in <c>TextData</c>.
/// </para>
/// </summary>
internal sealed class RefreshTraceSink : IDisposable
{
    // AS ObjectType codes carried on refresh progress events (observed on Power BI / Fabric).
    internal const int ObjectTypeCalculatedColumn = 802013;
    internal const int ObjectTypeAttributeHierarchy = 802014;
    internal const int ObjectTypePartition = 802015;
    internal const int ObjectTypeRelationship = 802016;
    internal const int ObjectTypeUserHierarchy = 802018;

    private static readonly string[] PhaseOrder =
        ["load", "hierarchies", "calculatedColumns", "relationships", "calculationScript", "sequencePoint", "commit"];

    private readonly TabularServer? _server;
    private readonly IReadOnlyList<string> _knownTables;
    private readonly IProgress<RefreshProgress>? _progress;
    private readonly TextWriter? _traceWriter;
    private readonly object _lock = new();
    private readonly Dictionary<string, TableAccumulator> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhaseAccumulator> _phases = new(StringComparer.Ordinal);
    // Per-table error messages captured from ProgressReportError events. Keyed by resolved table
    // name; values are the error texts (typically the engine's localized message). When the table
    // can't be resolved, the special key "" holds the unmatched errors.
    private readonly ConcurrentDictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private TabularTraceEventHandler? _handler;
    // Set by the TabularCommit end, the last event of a successful refresh.
    private readonly ManualResetEventSlim _committed = new(false);

    private RefreshTraceSink(TabularServer? server, IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress, TextWriter? traceWriter)
    {
        _server = server;
        // Longest first, so "Sales Detail" wins over "Sales" when matching paths and text.
        _knownTables = knownTables.OrderByDescending(t => t.Length).ToList();
        _progress = progress;
        _traceWriter = traceWriter;
    }

    /// <summary>
    /// Test-only constructor: builds a sink without an AMO server so unit tests can drive
    /// <see cref="Process(RefreshTraceEvent)"/> with synthetic events. The session trace
    /// is never attached in this mode.
    /// </summary>
    internal RefreshTraceSink(IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress = null, TextWriter? traceWriter = null)
        : this(server: null, knownTables, progress, traceWriter)
    {
    }

    /// <summary>Attaches a session trace to <paramref name="server"/> and returns a sink whose
    /// <see cref="BuildTableResults"/> yields per-table rollups after the refresh completes.
    /// <paramref name="knownTables"/> is the set of tables being refreshed, used to map child-object
    /// events (partitions, hierarchies, columns) back to their parent table.</summary>
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
            // Every column goes through the string indexer: AMO's typed getters (Duration,
            // IntegerData, StartTime, ...) throw when an event carries no value — Begin events
            // have no Duration — and that throw used to discard every event but the End ones.
            var ev = new RefreshTraceEvent(
                ParseTime(Column(e, TraceColumn.StartTime)) ?? ParseTime(Column(e, TraceColumn.CurrentTime)) ?? DateTime.UtcNow,
                e.EventClass,
                e.EventSubclass,
                ParseLong(Column(e, TraceColumn.Duration)),
                ParseLong(Column(e, TraceColumn.IntegerData)),
                Column(e, TraceColumn.ObjectName) ?? "",
                Column(e, TraceColumn.TextData),
                Column(e, TraceColumn.Error),
                Column(e, TraceColumn.ObjectPath),
                (int)ParseLong(Column(e, TraceColumn.ObjectType)));
            Process(ev);
        }
        catch
        {
            // Trace handler must never throw into the AMO event loop.
        }
    }

    private static string? Column(TabularTraceEventArgs e, TraceColumn column)
    {
        try { return e[column]; }
        catch { return null; }
    }

    internal static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static DateTime? ParseTime(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Blocks until the refresh's commit event has arrived, or <paramref name="timeout"/> elapses.
    /// Power BI / Fabric deliver session-trace events in batches seconds after the work they
    /// describe, so <c>server.Execute</c> returns before the trace has caught up; summarizing
    /// straight away reported zeros. Returns immediately when no trace is attached.
    /// </summary>
    public bool WaitForCommit(TimeSpan timeout)
    {
        if (_handler is null)
            return false;
        if (!_committed.Wait(timeout))
            return false;
        // The commit's second phase and any stragglers ride in the same or the next batch.
        Thread.Sleep(CommitSettle);
        return true;
    }

    private static readonly TimeSpan CommitSettle = TimeSpan.FromMilliseconds(250);

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
        // Appended last so the leading columns stay positional.
        if (!string.IsNullOrEmpty(e.ObjectPath))
            line.Append('\t').Append("path=").Append(e.ObjectPath);
        if (e.ObjectType != 0)
            line.Append('\t').Append("type=").Append(e.ObjectType);
        lock (_traceWriter)
            _traceWriter.WriteLine(line.ToString());
    }

    /// <summary>
    /// Routes one refresh trace event into the accumulators and reports a live snapshot.
    /// Partition-level events (<c>ExecuteSql</c>, <c>ReadData</c>, compression, the partition's
    /// <c>TabularRefresh</c>) feed per-partition load stats; hierarchy and calculated-column
    /// <c>TabularRefresh</c> ends feed the table's post-load time; relationships, the calculation
    /// script, the sequence point, and the commit feed model-level phases.
    /// </summary>
    internal void HandleProgress(RefreshTraceEvent e)
    {
        if (e.EventClass == AsTraceEventClass.ProgressReportError)
        {
            // Capture per-table errors before any other routing: ProgressReportError events carry
            // the failing object/table context that server.Execute's XmlaError messages lack.
            CaptureError(e);
            return;
        }

        if (e.EventClass is AsTraceEventClass.ExecuteMdxScriptBegin or AsTraceEventClass.ExecuteMdxScriptEnd)
        {
            if (e.EventSubclass == AsTraceEventSubclass.MdxScript)
                ModelPhase(e, "calculationScript", "running calculation script");
            return;
        }

        if (e.EventClass is not (AsTraceEventClass.ProgressReportBegin
            or AsTraceEventClass.ProgressReportCurrent
            or AsTraceEventClass.ProgressReportEnd))
            return;

        switch (e.EventSubclass)
        {
            case AsTraceEventSubclass.TabularCommit or AsTraceEventSubclass.Commit:
                Commit(e);
                return;
            case AsTraceEventSubclass.TabularSequencePoint:
                ModelPhase(e, "sequencePoint", "finalizing");
                return;
            case AsTraceEventSubclass.RelationshipBuildPrepare:
                // Only a Begin fires; the relationship's TabularRefresh end carries the duration.
                ModelPhase(e, "relationships", "building relationships", recordEnd: false);
                return;
        }

        if (e.ObjectType == ObjectTypeRelationship && e.EventSubclass == AsTraceEventSubclass.TabularRefresh)
        {
            ModelPhase(e, "relationships", "building relationships");
            return;
        }

        var (table, child) = Resolve(e);
        if (table is null)
            return;
        if (IsPartitionEvent(e, table, child))
            PartitionEvent(e, table, child ?? table);
        else if (IsPostLoadEvent(e))
            PostLoadEvent(e, table);
    }

    private static bool IsPostLoadEvent(RefreshTraceEvent e)
        => e.EventSubclass == AsTraceEventSubclass.TabularRefresh
           && e.ObjectType is ObjectTypeAttributeHierarchy or ObjectTypeUserHierarchy or ObjectTypeCalculatedColumn;

    /// <summary>
    /// Partition-level work. With an <c>ObjectType</c> that is simply "is it a partition"; without
    /// one (older servers, synthetic fixtures) load subclasses always are, and a
    /// <c>TabularRefresh</c> only when it names the table itself — child hierarchies and columns
    /// also emit <c>TabularRefresh</c> and must not overwrite the partition total.
    /// </summary>
    private static bool IsPartitionEvent(RefreshTraceEvent e, string table, string? child)
    {
        if (e.ObjectType != 0)
            return e.ObjectType == ObjectTypePartition;

        return e.EventSubclass switch
        {
            AsTraceEventSubclass.ExecuteSql or AsTraceEventSubclass.ReadData => true,
            AsTraceEventSubclass.TabularRefresh => string.Equals(child, table, StringComparison.Ordinal),
            _ => false,
        };
    }

    private void PartitionEvent(RefreshTraceEvent e, string table, string partitionName)
    {
        RefreshProgress? report;
        lock (_lock)
        {
            var acc = Table(table);
            var partition = acc.Partition(partitionName);
            var isEnd = e.EventClass == AsTraceEventClass.ProgressReportEnd;
            var key = $"{partitionName}/{e.EventSubclass}";

            if (e.EventClass == AsTraceEventClass.ProgressReportBegin)
                acc.Active.Add(key);
            else if (isEnd)
                acc.Active.Remove(key);

            switch (e.EventSubclass)
            {
                case AsTraceEventSubclass.ExecuteSql when isEnd:
                    // Source query (SQL or M) end: this is the "Query" column. Carries no row count.
                    partition.QueryMs = e.Duration;
                    break;
                case AsTraceEventSubclass.ReadData when isEnd:
                    // Data read end: this is the "Read" column. IntegerData is the partition's
                    // authoritative row count.
                    partition.ReadMs = e.Duration;
                    if (e.IntegerData > 0) partition.Rows = e.IntegerData;
                    break;
                case AsTraceEventSubclass.TabularRefresh when isEnd:
                    // The partition's whole refresh, including commit/lock overhead. Kept as the
                    // Total fallback when neither ExecuteSql nor ReadData fired.
                    partition.RefreshMs = e.Duration;
                    partition.Completed = true;
                    AddInterval("load", e);
                    // The partition is done even if a sub-step's End never arrived; a leftover
                    // Begin must not hold the table "in progress" for the rest of the refresh.
                    acc.Active.RemoveWhere(k => k.StartsWith(partitionName + "/", StringComparison.Ordinal));
                    break;
            }

            // Live row count while data streams in (ProgressReportCurrent; common on-prem).
            if (e.EventClass == AsTraceEventClass.ProgressReportCurrent && e.IntegerData > 0)
                partition.Rows = e.IntegerData;

            acc.Phase = PartitionPhase(e.EventSubclass) ?? acc.Phase;
            report = _progress is null ? null : Snapshot(acc, partitionName);
        }

        if (report is not null)
            _progress!.Report(report);
    }

    private static string? PartitionPhase(AsTraceEventSubclass subclass) => subclass switch
    {
        AsTraceEventSubclass.ExecuteSql => "query",
        AsTraceEventSubclass.ReadData => "read",
        AsTraceEventSubclass.Process or AsTraceEventSubclass.VertiPaq or AsTraceEventSubclass.CompressSegment
            or AsTraceEventSubclass.AnalyzeEncodeData => "compress",
        _ => null,
    };

    private void PostLoadEvent(RefreshTraceEvent e, string table)
    {
        var calculated = e.ObjectType == ObjectTypeCalculatedColumn;
        RefreshProgress? report;
        lock (_lock)
        {
            var acc = Table(table);
            var key = $"{e.ObjectType}/{e.ObjectName}";
            if (e.EventClass == AsTraceEventClass.ProgressReportBegin)
            {
                acc.Active.Add(key);
            }
            else if (e.EventClass == AsTraceEventClass.ProgressReportEnd)
            {
                acc.Active.Remove(key);
                acc.ProcessMs += e.Duration;
                AddInterval(calculated ? "calculatedColumns" : "hierarchies", e);
            }

            acc.Phase = calculated ? "calculated columns" : "hierarchies";
            report = _progress is null ? null : Snapshot(acc, partition: null);
        }

        if (report is not null)
            _progress!.Report(report);
    }

    private void ModelPhase(RefreshTraceEvent e, string phase, string label, bool recordEnd = true)
    {
        var isEnd = e.EventClass is AsTraceEventClass.ProgressReportEnd or AsTraceEventClass.ExecuteMdxScriptEnd;
        if (isEnd && recordEnd)
        {
            lock (_lock)
                AddInterval(phase, e);
        }

        _progress?.Report(new RefreshProgress(Table: null, RowsRead: null, Phase: label, Completed: false));
    }

    private void Commit(RefreshTraceEvent e)
    {
        if (e.EventClass == AsTraceEventClass.ProgressReportEnd && e.EventSubclass == AsTraceEventSubclass.TabularCommit)
        {
            // Commit marks the overall refresh as done — mark every tracked table completed.
            lock (_lock)
            {
                AddInterval("commit", e);
                _committed.Set();
                foreach (var acc in _tables.Values)
                {
                    acc.Active.Clear();
                    foreach (var partition in acc.Partitions.Values)
                        partition.Completed = true;
                }
            }
            _progress?.Report(new RefreshProgress(Table: "", RowsRead: null, Phase: "commit", Completed: true));
            return;
        }

        _progress?.Report(new RefreshProgress(Table: null, RowsRead: null, Phase: "committing", Completed: false));
    }

    // Caller holds _lock.
    private static RefreshProgress Snapshot(TableAccumulator acc, string? partition)
    {
        var rows = acc.Partitions.Values.Sum(p => p.Rows);
        var completed = acc.Active.Count == 0 && acc.Partitions.Values.Any(p => p.Completed);
        return new RefreshProgress(
            Table: acc.Name,
            RowsRead: rows > 0 ? rows : null,
            Phase: completed ? "done" : acc.Phase ?? "processing",
            Completed: completed,
            Partition: acc.Partitions.Count > 1 ? partition : null);
    }

    // Caller holds _lock.
    private TableAccumulator Table(string name)
    {
        if (!_tables.TryGetValue(name, out var acc))
            _tables[name] = acc = new TableAccumulator(name);
        return acc;
    }

    // Caller holds _lock. End events carry their start time and duration.
    private void AddInterval(string phase, RefreshTraceEvent e)
    {
        if (!_phases.TryGetValue(phase, out var acc))
            _phases[phase] = acc = new PhaseAccumulator();
        acc.Count++;
        acc.Intervals.Add((e.StartTime, e.StartTime.AddMilliseconds(Math.Max(e.Duration, 0))));
    }

    /// <summary>
    /// Resolves the table (and the child object — partition, column, hierarchy) an event belongs
    /// to. <c>ObjectPath</c> is authoritative when present; otherwise <c>ObjectName</c> is tried as a
    /// table name, then the known table names are searched for in <c>TextData</c> (localized
    /// descriptions like "Processing of hierarchy 'X' in table 'Datoer' completed" embed it).
    /// </summary>
    private (string? Table, string? Child) Resolve(RefreshTraceEvent e)
    {
        if (!string.IsNullOrEmpty(e.ObjectPath))
        {
            var fromPath = ResolvePath(e.ObjectPath);
            if (fromPath.Table is not null)
                return fromPath;
        }

        var table = ResolveTableName(e);
        return table.Length == 0 ? (null, null) : (table, e.ObjectName.Length > 0 ? e.ObjectName : null);
    }

    /// <summary>
    /// Splits <c>&lt;db&gt;.Model.&lt;Table&gt;[.&lt;Child&gt;]</c> against the known tables. Table
    /// names may contain dots, so it matches a known name rather than splitting on the dot.
    /// </summary>
    internal (string? Table, string? Child) ResolvePath(string objectPath)
    {
        const string marker = ".Model.";
        var at = objectPath.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return (null, null);

        var rest = objectPath[(at + marker.Length)..];
        foreach (var known in _knownTables)
        {
            if (string.Equals(rest, known, StringComparison.Ordinal))
                return (known, null);
            if (rest.Length > known.Length + 1 && rest.StartsWith(known, StringComparison.Ordinal) && rest[known.Length] == '.')
                return (known, rest[(known.Length + 1)..]);
        }
        return (null, null);
    }

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

        // Fallback: scan TextData for any known table name, longest first to avoid partial matches.
        var text = e.TextData;
        if (string.IsNullOrEmpty(text))
            return "";

        return _knownTables.FirstOrDefault(t => text.Contains(t, StringComparison.Ordinal)) ?? "";
    }

    public IReadOnlyList<RefreshTableResult> BuildTableResults()
    {
        lock (_lock)
        {
            return _tables.Values
                .Where(a => a.Partitions.Count > 0)
                .Select(a => a.ToResult())
                .OrderBy(t => t.TotalMs > 0 ? t.TotalMs : long.MaxValue)
                .ThenBy(t => t.Table, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Model-level phases in processing order, each with its wall-clock duration (overlapping
    /// events merged). Null when the trace captured no phase at all.
    /// </summary>
    public IReadOnlyList<RefreshPhaseResult>? BuildPhases()
    {
        lock (_lock)
        {
            var phases = PhaseOrder
                .Where(_phases.ContainsKey)
                .Select(name => new RefreshPhaseResult(name, _phases[name].Count, _phases[name].WallClockMs()))
                .ToList();
            return phases.Count > 0 ? phases : null;
        }
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

        // An empty key means we couldn't associate the error with a known table. The caller
        // surfaces these separately so they still appear in the failure message.
        var key = Resolve(e).Table ?? "";
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
        _committed.Dispose();
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
        public Dictionary<string, PartitionAccumulator> Partitions { get; } = new(StringComparer.Ordinal);
        // Begun-but-not-ended work (per partition subclass or post-load object); empty = idle.
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        public long ProcessMs;
        public string? Phase;

        public PartitionAccumulator Partition(string name)
        {
            if (!Partitions.TryGetValue(name, out var partition))
                Partitions[name] = partition = new PartitionAccumulator(name);
            return partition;
        }

        public RefreshTableResult ToResult()
        {
            var partitions = Partitions.Values
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => p.ToResult())
                .ToList();
            return new RefreshTableResult(
                Name,
                partitions.Sum(p => p.Rows),
                partitions.Sum(p => p.QueryMs),
                partitions.Sum(p => p.ReadMs),
                partitions.Sum(p => p.TotalMs),
                ProcessMs,
                partitions);
        }
    }

    private sealed class PartitionAccumulator
    {
        public PartitionAccumulator(string name) => Name = name;
        public string Name { get; }
        public long Rows;
        public long QueryMs;
        public long ReadMs;
        // TabularRefresh partition-end duration. Total fallback when neither ExecuteSql nor
        // ReadData fired (defensive — never observed in real traces).
        public long RefreshMs;
        public bool Completed;

        /// <summary>
        /// Total = Query + Read (matches the reference CLI's arithmetic for the majority of
        /// partitions). Falls back to RefreshMs when neither phase event fired.
        /// </summary>
        public RefreshPartitionResult ToResult()
        {
            var total = (QueryMs > 0 || ReadMs > 0) ? QueryMs + ReadMs : RefreshMs;
            return new RefreshPartitionResult(Name, Rows, QueryMs, ReadMs, total);
        }
    }

    private sealed class PhaseAccumulator
    {
        public int Count;
        public List<(DateTime Start, DateTime End)> Intervals { get; } = [];

        /// <summary>Union of the event intervals, so parallel work is counted once.</summary>
        public long WallClockMs()
        {
            var total = TimeSpan.Zero;
            DateTime? start = null, end = null;
            foreach (var (s, e) in Intervals.OrderBy(i => i.Start))
            {
                if (end is null || s > end)
                {
                    if (start is not null) total += end!.Value - start.Value;
                    (start, end) = (s, e);
                }
                else if (e > end)
                {
                    end = e;
                }
            }
            if (start is not null) total += end!.Value - start.Value;
            return (long)Math.Round(total.TotalMilliseconds);
        }
    }
}

/// <summary>
/// AMO-free projection of <see cref="TabularTraceEventArgs"/>. Introduced so the sink's
/// event-handling logic can be unit-tested with synthetic events instead of a live server.
/// </summary>
/// <param name="ObjectPath">Dotted path <c>&lt;db&gt;.Model.&lt;Table&gt;.&lt;Child&gt;</c>, when supplied.</param>
/// <param name="ObjectType">AS object-type code (see <see cref="RefreshTraceSink"/> constants); 0 when absent.</param>
internal sealed record RefreshTraceEvent(
    DateTime StartTime,
    AsTraceEventClass EventClass,
    AsTraceEventSubclass EventSubclass,
    long Duration,
    long IntegerData,
    string ObjectName,
    string? TextData,
    string? Error,
    string? ObjectPath = null,
    int ObjectType = 0);
