using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices;
using Tomix.Core.Models;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace Tomix.Provider.Tom;

/// <summary>
/// Captures DAX/DMV server timings and query plans for a single query connection by creating a
/// dedicated server-level XMLA trace. Unlike <see cref="RefreshTraceSink"/> (which piggybacks on
/// <c>Server.SessionTrace</c> because the refresh runs on the AMO session), a query runs on a
/// separate ADOMD connection, so we open our own core <see cref="Server"/>, create a
/// <see cref="Trace"/> filtered to the query's <c>SessionID</c>, and subscribe to the query-perf
/// event classes — the approach DAX Studio uses. Tracing requires admin rights on the endpoint;
/// when unavailable the sink degrades to a no-op (the query still runs) with a one-line warning.
/// </summary>
internal sealed class TomQueryTraceSink : IDisposable
{
    // Marker embedded in the cache warm-up query so its QueryEnd doesn't count as a run.
    internal const string InternalMarker = "<<tomix-internal>>";

    private readonly Server? _server;
    private readonly Trace? _trace;
    private readonly string _sessionId;
    private readonly TextWriter? _rawWriter;
    private TraceEventHandler? _handler;

    private readonly object _lock = new();
    private long _seDurationMs;
    private long _seCpuMs;
    private int _seQueryCount;
    private int _cacheHits;
    private string? _logicalPlan;
    private string? _physicalPlan;
    private QueryTimings? _lastRunTimings;
    private TaskCompletionSource<bool>? _runComplete;

    private TomQueryTraceSink(Server? server, Trace? trace, string sessionId, TextWriter? rawWriter)
    {
        _server = server;
        _trace = trace;
        _sessionId = sessionId;
        _rawWriter = rawWriter;
    }

    /// <summary>Test-only constructor: builds a sink with no live server so unit tests can drive
    /// <see cref="Process(QueryTraceEvent)"/> with synthetic events.</summary>
    internal TomQueryTraceSink() : this(server: null, trace: null, sessionId: "", rawWriter: null)
    {
    }

    /// <summary>True when a live trace is attached (admin rights present); false in degraded mode.</summary>
    public bool Active => _trace is not null;

    /// <summary>
    /// Opens a dedicated trace connection to <paramref name="connectionString"/>, creates a trace
    /// filtered client-side to <paramref name="sessionId"/>, and starts it. Returns null (with a
    /// stderr warning) when the trace cannot be created — the caller then runs without timings.
    /// </summary>
    /// <param name="tokenFactory">Supplies an access token for remote endpoints; null for local instances.</param>
    /// <param name="wantPlan">Also subscribe to <c>DAXQueryPlan</c> events for logical/physical plans.</param>
    /// <param name="rawWriter">Optional sink for a raw per-event dump (from <c>--trace &lt;path&gt;</c>).</param>
    public static TomQueryTraceSink? Attach(
        string connectionString,
        Func<AsAccessToken>? tokenFactory,
        string sessionId,
        bool wantPlan,
        TextWriter? rawWriter)
    {
        Server? server = null;
        try
        {
            server = new Server();
            if (tokenFactory is not null)
            {
                server.AccessToken = tokenFactory();
                server.OnAccessTokenExpired = _ => tokenFactory();
            }

            server.Connect(connectionString);

            var trace = server.Traces.Add(TraceName(sessionId));
            AddEvent(trace, TraceEventClass.QueryEnd, TimingColumns);
            AddEvent(trace, TraceEventClass.VertiPaqSEQueryEnd, TimingColumns);
            AddEvent(trace, TraceEventClass.VertiPaqSEQueryCacheMatch, CommonColumns);
            AddEvent(trace, TraceEventClass.DirectQueryEnd, TimingColumns);
            if (wantPlan)
                AddEvent(trace, TraceEventClass.DAXQueryPlan, CommonColumns);

            var sink = new TomQueryTraceSink(server, trace, sessionId, rawWriter);
            sink._handler = sink.OnEvent;
            trace.OnEvent += sink._handler;
            trace.Update(UpdateOptions.Default, UpdateMode.CreateOrReplace);
            trace.Start();
            return sink;
        }
        catch (Exception ex)
        {
            // Best-effort, exactly like RefreshTraceSink: tracing needs admin rights and is not
            // available on shared-capacity Power BI. Warn once and let the query run without timings.
            try { Console.Error.WriteLine($"[tomix] query trace unavailable: {ex.Message}"); } catch { }
            try { server?.Dispose(); } catch { }
            return null;
        }
    }

    private static readonly TraceColumn[] CommonColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.CurrentTime,
        TraceColumn.TextData, TraceColumn.SessionID, TraceColumn.Spid
    ];

    private static readonly TraceColumn[] TimingColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.CurrentTime,
        TraceColumn.StartTime, TraceColumn.EndTime, TraceColumn.Duration, TraceColumn.CpuTime,
        TraceColumn.IntegerData, TraceColumn.TextData, TraceColumn.SessionID, TraceColumn.Spid
    ];

    private static void AddEvent(Trace trace, TraceEventClass eventClass, TraceColumn[] columns)
    {
        var traceEvent = new TraceEvent(eventClass);
        foreach (var column in columns)
            traceEvent.Columns.Add(column);
        trace.Events.Add(traceEvent);
    }

    private static string TraceName(string sessionId)
    {
        var safe = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        return "tomix_query_" + (safe.Length > 0 ? safe : "session");
    }

    /// <summary>
    /// Begins a new run: clears the per-run accumulators and arms the completion signal awaited by
    /// <see cref="WaitForRun"/>. Called by the executor immediately before executing the query so
    /// events from the cache warm-up (which precedes it) are never attributed to the run.
    /// </summary>
    public void StartRun()
    {
        lock (_lock)
        {
            _seDurationMs = 0;
            _seCpuMs = 0;
            _seQueryCount = 0;
            _cacheHits = 0;
            _logicalPlan = null;
            _physicalPlan = null;
            _lastRunTimings = null;
            _runComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Blocks until the run's <c>QueryEnd</c> event arrives (trace events land on a background
    /// thread), then returns the aggregated timings — or null if none arrived within
    /// <paramref name="timeout"/>.
    /// </summary>
    public QueryTimings? WaitForRun(TimeSpan timeout)
    {
        var completion = _runComplete;
        try { completion?.Task.Wait(timeout); }
        catch { /* never faulted; ignore */ }
        lock (_lock)
            return _lastRunTimings;
    }

    /// <summary>Logical/physical plans captured across the run(s), or null when none were seen.</summary>
    public IReadOnlyList<QueryPlan>? BuildPlans()
    {
        lock (_lock)
        {
            var plans = new List<QueryPlan>(2);
            if (!string.IsNullOrEmpty(_logicalPlan))
                plans.Add(new QueryPlan("logical", _logicalPlan!));
            if (!string.IsNullOrEmpty(_physicalPlan))
                plans.Add(new QueryPlan("physical", _physicalPlan!));
            return plans.Count > 0 ? plans : null;
        }
    }

    private void OnEvent(object? sender, TraceEventArgs e)
    {
        try
        {
            // Correlate to our query's ADOMD session; drop events from other sessions. When either
            // side lacks a SessionID we accept the event (a CLI runs one query at a time).
            if (_sessionId.Length > 0 && !string.IsNullOrEmpty(e.SessionID)
                && !string.Equals(e.SessionID, _sessionId, StringComparison.OrdinalIgnoreCase))
                return;

            var text = e.TextData;
            // Skip our own cache warm-up query so its QueryEnd doesn't complete the run early.
            if (!string.IsNullOrEmpty(text) && text.Contains(InternalMarker, StringComparison.Ordinal))
                return;

            WriteRaw(e);
            Process(new QueryTraceEvent(e.EventClass, e.EventSubclass, e.Duration, e.CpuTime, e.IntegerData, text));
        }
        catch
        {
            // A trace handler must never throw back into the AMO event loop.
        }
    }

    private void WriteRaw(TraceEventArgs e)
    {
        if (_rawWriter is null) return;
        var line = new StringBuilder()
            .Append(e.CurrentTime.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
            .Append(e.EventClass).Append('/').Append(e.EventSubclass).Append('\t')
            .Append("dur=").Append(e.Duration).Append('\t')
            .Append("cpu=").Append(e.CpuTime).Append('\t')
            .Append("int=").Append(e.IntegerData);
        if (!string.IsNullOrEmpty(e.TextData))
            line.Append('\t').Append(e.TextData.Replace('\n', ' ').Replace('\r', ' ').Trim());
        _rawWriter.WriteLine(line.ToString());
    }

    /// <summary>
    /// Aggregates one projected trace event. Storage-engine time is the sum of leaf
    /// <c>VertiPaqScan</c> durations (batch/outer scans are ignored to avoid double-counting);
    /// <c>QueryEnd</c> closes the run with Total and a first-order FE = max(Total − SE, 0).
    /// The only AMO-free entry point, so all aggregation is unit-testable with synthetic events.
    /// </summary>
    internal void Process(QueryTraceEvent e)
    {
        lock (_lock)
        {
            switch (e.EventClass)
            {
                case TraceEventClass.VertiPaqSEQueryEnd:
                    if (e.EventSubclass == TraceEventSubclass.VertiPaqScan)
                    {
                        _seDurationMs += e.Duration;
                        _seCpuMs += e.CpuTime;
                        _seQueryCount++;
                    }
                    break;

                case TraceEventClass.DirectQueryEnd:
                    _seDurationMs += e.Duration;
                    _seCpuMs += e.CpuTime;
                    _seQueryCount++;
                    break;

                case TraceEventClass.VertiPaqSEQueryCacheMatch:
                    _cacheHits++;
                    break;

                case TraceEventClass.DAXQueryPlan:
                    if (e.EventSubclass == TraceEventSubclass.DAXVertiPaqLogicalPlan)
                        _logicalPlan = e.TextData;
                    else if (e.EventSubclass == TraceEventSubclass.DAXVertiPaqPhysicalPlan)
                        _physicalPlan = e.TextData;
                    break;

                case TraceEventClass.QueryEnd:
                    var total = e.Duration;
                    var fe = Math.Max(total - _seDurationMs, 0);
                    _lastRunTimings = new QueryTimings(
                        total, e.CpuTime, fe, _seDurationMs, _seCpuMs, _seQueryCount, _cacheHits);
                    _runComplete?.TrySetResult(true);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_server is null) return;
        try
        {
            if (_trace is not null)
            {
                try { if (_handler is not null) _trace.OnEvent -= _handler; } catch { }
                try { if (_trace.IsStarted) _trace.Stop(); } catch { }
                try { _trace.Drop(); } catch { }
            }
            if (_server.Connected)
                _server.Disconnect();
            _server.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>
/// AMO-free projection of <see cref="TraceEventArgs"/> for the query-perf events the sink consumes.
/// Lets the aggregation in <see cref="TomQueryTraceSink.Process"/> be unit-tested without a server.
/// </summary>
internal sealed record QueryTraceEvent(
    TraceEventClass EventClass,
    TraceEventSubclass EventSubclass,
    long Duration,
    long CpuTime,
    long IntegerData,
    string? TextData);
