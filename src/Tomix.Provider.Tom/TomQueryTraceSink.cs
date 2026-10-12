using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.AdomdClient;
using Tomix.Core.Models;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace Tomix.Provider.Tom;

/// <summary>
/// Captures DAX/DMV server timings for a single query connection by creating a
/// dedicated server-level XMLA trace. Unlike <see cref="RefreshTraceSink"/> (which piggybacks on
/// <c>Server.SessionTrace</c> because the refresh runs on the AMO session), a query runs on a
/// separate ADOMD connection, so we open our own core <see cref="Server"/>, create a
/// <see cref="Trace"/>, and subscribe to the query-perf event classes — the approach DAX Studio
/// uses. Tracing requires write access to the model; when unavailable the sink degrades to a
/// no-op (the query still runs) with a one-line warning.
/// <para>
/// Three details matter on Power BI / Fabric XMLA endpoints, all mirrored from DAX Studio:
/// each event subscribes only to columns the server reports it supports (Fabric rejects the whole
/// trace for one unsupported pair, e.g. <c>DirectQueryEnd</c> + <c>EventSubclass</c>); the trace
/// carries a server-side filter on the query's <c>SessionID</c> <em>or</em> its unique
/// <c>Application Name</c>; and the query is not run until the trace has delivered a first
/// (heartbeat) event, since traces there take a moment to go live.
/// </para>
/// </summary>
internal sealed class TomQueryTraceSink : IDisposable
{
    // Marker embedded in the cache warm-up query so its QueryEnd doesn't count as a run.
    internal const string InternalMarker = "<<tomix-internal>>";

    /// <summary>How long <see cref="Attach"/> pings the server waiting for the trace's first event.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);

    private const string EngineNamespace = "http://schemas.microsoft.com/analysisservices/2003/engine";

    private readonly Server? _server;
    private readonly Trace? _trace;
    private readonly string _sessionId;
    private readonly string _applicationName;
    private readonly TextWriter? _rawWriter;
    private TraceEventHandler? _handler;

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _firstEvent = new(false);
    private int _foreignEvents;
    private long _seDurationMs;
    private long _seCpuMs;
    private int _seQueryCount;
    private int _cacheHits;
    private QueryTimings? _lastRunTimings;
    private TaskCompletionSource<bool>? _runComplete;

    private TomQueryTraceSink(Server? server, Trace? trace, string sessionId, string applicationName, TextWriter? rawWriter)
    {
        _server = server;
        _trace = trace;
        _sessionId = sessionId;
        _applicationName = applicationName;
        _rawWriter = rawWriter;
    }

    /// <summary>Test-only constructor: builds a sink with no live server so unit tests can drive
    /// <see cref="Process(QueryTraceEvent)"/> with synthetic events.</summary>
    internal TomQueryTraceSink() : this(server: null, trace: null, sessionId: "", applicationName: "", rawWriter: null)
    {
    }

    /// <summary>True when a live trace is attached (write access present); false in degraded mode.</summary>
    public bool Active => _trace is not null;

    /// <summary>
    /// Events that arrived from another session/application and were dropped by the client-side
    /// correlation check. Surfaced in the "no timings" diagnostic: a non-zero count means the
    /// trace works but the events could not be tied to our query.
    /// </summary>
    public int ForeignEventCount => Volatile.Read(ref _foreignEvents);

    /// <summary>
    /// Opens a dedicated trace connection to <paramref name="connectionString"/>, creates a trace
    /// filtered to <paramref name="queryConnection"/>'s session and <paramref name="applicationName"/>,
    /// starts it, and waits until it delivers its first event. Returns null, with the reason in
    /// <paramref name="unavailableReason"/>, when the trace cannot be created or never goes live —
    /// the caller then runs without timings.
    /// </summary>
    /// <param name="queryConnection">The open query connection; used for the supported-columns discover
    /// and the start-up heartbeat, and its <c>SessionID</c> scopes the trace.</param>
    /// <param name="connectionString">The query connection's string (including its <c>Application Name</c>).</param>
    /// <param name="applicationName">The unique <c>Application Name</c> set on the query connection.</param>
    /// <param name="tokenFactory">Supplies an access token for remote endpoints; null for local instances.</param>
    /// <param name="rawWriter">Optional sink for a raw per-event dump (from <c>--trace &lt;path&gt;</c>).</param>
    public static TomQueryTraceSink? Attach(
        AdomdConnection queryConnection,
        string connectionString,
        string applicationName,
        Func<AsAccessToken>? tokenFactory,
        TextWriter? rawWriter,
        out string? unavailableReason)
    {
        unavailableReason = null;
        Server? server = null;
        TomQueryTraceSink? sink = null;
        try
        {
            var supported = DiscoverSupportedColumns(queryConnection);

            server = new Server();
            if (tokenFactory is not null)
            {
                server.AccessToken = tokenFactory();
                server.OnAccessTokenExpired = _ => tokenFactory();
            }

            server.Connect(connectionString);

            var sessionId = queryConnection.SessionID ?? "";
            var trace = server.Traces.Add(TraceName(sessionId));
            foreach (var (eventClass, columns) in BuildEventColumns(supported))
            {
                var traceEvent = new TraceEvent(eventClass);
                foreach (var column in columns)
                    traceEvent.Columns.Add(column);
                trace.Events.Add(traceEvent);
            }

            var filter = new XmlDocument { XmlResolver = null };
            filter.LoadXml(BuildFilterXml(sessionId, applicationName));
            trace.Filter = filter;
            // Safety net: a trace orphaned by a crash stops on its own instead of running server-side forever.
            trace.StopTime = DateTime.UtcNow.AddHours(1);

            sink = new TomQueryTraceSink(server, trace, sessionId, applicationName, rawWriter);
            sink._handler = sink.OnEvent;
            trace.OnEvent += sink._handler;
            trace.Update(UpdateOptions.Default, UpdateMode.CreateOrReplace);
            trace.Start();

            if (!sink.WaitUntilLive(() => Heartbeat(queryConnection)))
            {
                unavailableReason =
                    $"the trace was created but delivered no events within {StartTimeout.TotalSeconds:0}s.";
                sink.Dispose();
                return null;
            }

            return sink;
        }
        catch (Exception ex)
        {
            // Best-effort, exactly like RefreshTraceSink: tracing needs write access and is not
            // available on shared-capacity Power BI. Let the query run without timings.
            unavailableReason = TomModelQueryExecutor.ServerReason(ex.Message);
            if (sink is not null)
                sink.Dispose();
            else
                try { server?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Pings the server with a discover (as DAX Studio does) until the trace delivers any event, so
    /// the query only runs once the trace is live. Returns false if nothing arrived in time.
    /// </summary>
    private bool WaitUntilLive(System.Action heartbeat)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try { heartbeat(); } catch { /* a failed ping just means we wait for the next one */ }
            if (_firstEvent.Wait(HeartbeatInterval))
                return true;
        }
        return _firstEvent.IsSet;
    }

    private static void Heartbeat(AdomdConnection connection)
        => _ = connection.GetSchemaDataSet("MDSCHEMA_CUBES", null);

    /// <summary>
    /// Reads <c>DISCOVER_TRACE_EVENT_CATEGORIES</c> into the columns each event supports on this
    /// server. Returns null when the discover fails; <see cref="BuildEventColumns"/> then falls back
    /// to a conservative static set.
    /// </summary>
    private static IReadOnlyDictionary<TraceEventClass, HashSet<TraceColumn>>? DiscoverSupportedColumns(AdomdConnection connection)
    {
        try
        {
            using var command = new AdomdCommand("SELECT * FROM $SYSTEM.DISCOVER_TRACE_EVENT_CATEGORIES", connection);
            using var reader = command.ExecuteReader();
            var categories = new List<string>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    categories.Add(reader.GetString(0));
            }
            var parsed = ParseSupportedColumns(categories);
            return parsed.Count > 0 ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the <c>EVENTCATEGORY</c> XML documents returned by <c>DISCOVER_TRACE_EVENT_CATEGORIES</c>
    /// into event ID → supported column IDs. Malformed documents are skipped.
    /// </summary>
    internal static Dictionary<TraceEventClass, HashSet<TraceColumn>> ParseSupportedColumns(IEnumerable<string> categoryXml)
    {
        var result = new Dictionary<TraceEventClass, HashSet<TraceColumn>>();
        foreach (var xml in categoryXml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            try { doc.LoadXml(xml); }
            catch (XmlException) { continue; }

            var events = doc.SelectNodes("/EVENTCATEGORY/EVENTLIST/EVENT");
            if (events is null) continue;
            foreach (XmlNode evt in events)
            {
                if (!int.TryParse(evt.SelectSingleNode("ID")?.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
                    continue;

                var columns = new HashSet<TraceColumn>();
                var columnIds = evt.SelectNodes("EVENTCOLUMNLIST/EVENTCOLUMN/ID");
                if (columnIds is not null)
                {
                    foreach (XmlNode id in columnIds)
                    {
                        if (int.TryParse(id.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columnId))
                            columns.Add((TraceColumn)columnId);
                    }
                }
                result[(TraceEventClass)eventId] = columns;
            }
        }
        return result;
    }

    private static readonly TraceColumn[] CommonColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.CurrentTime,
        TraceColumn.TextData, TraceColumn.SessionID, TraceColumn.ApplicationName, TraceColumn.Spid
    ];

    private static readonly TraceColumn[] TimingColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.CurrentTime,
        TraceColumn.StartTime, TraceColumn.EndTime, TraceColumn.Duration, TraceColumn.CpuTime,
        TraceColumn.IntegerData, TraceColumn.TextData, TraceColumn.SessionID,
        TraceColumn.ApplicationName, TraceColumn.Spid
    ];

    /// <summary>
    /// Columns known to be rejected when the server's supported set could not be discovered.
    /// Fabric XMLA rejects <c>DirectQueryEnd</c> + <c>EventSubclass</c> (issue #94), and SSAS 2025
    /// dropped <c>ApplicationName</c> from the storage-engine events.
    /// </summary>
    private static readonly Dictionary<TraceEventClass, TraceColumn[]> FallbackExclusions = new()
    {
        [TraceEventClass.DirectQueryEnd] = [TraceColumn.EventSubclass, TraceColumn.ApplicationName],
        [TraceEventClass.VertiPaqSEQueryEnd] = [TraceColumn.ApplicationName],
        [TraceEventClass.VertiPaqSEQueryCacheMatch] = [TraceColumn.ApplicationName],
    };

    /// <summary>
    /// The events the trace subscribes to and, for each, the desired columns narrowed to what
    /// <paramref name="supported"/> says the server accepts. An event the server does not list is
    /// skipped (except the always-needed <c>QueryEnd</c>). With no discover result, a static
    /// fallback drops the column pairs known to be rejected.
    /// </summary>
    internal static IReadOnlyList<(TraceEventClass Event, IReadOnlyList<TraceColumn> Columns)> BuildEventColumns(
        IReadOnlyDictionary<TraceEventClass, HashSet<TraceColumn>>? supported)
    {
        var wanted = new List<(TraceEventClass, TraceColumn[])>
        {
            // DiscoverBegin only exists so the start-up heartbeat can prove the trace is live.
            (TraceEventClass.DiscoverBegin, CommonColumns),
            (TraceEventClass.QueryEnd, TimingColumns),
            (TraceEventClass.VertiPaqSEQueryEnd, TimingColumns),
            (TraceEventClass.VertiPaqSEQueryCacheMatch, CommonColumns),
            (TraceEventClass.DirectQueryEnd, TimingColumns),
        };

        var result = new List<(TraceEventClass, IReadOnlyList<TraceColumn>)>(wanted.Count);
        foreach (var (eventClass, desired) in wanted)
        {
            IReadOnlyList<TraceColumn> columns;
            if (supported is null)
            {
                var excluded = FallbackExclusions.GetValueOrDefault(eventClass) ?? [];
                columns = desired.Where(c => !excluded.Contains(c)).ToArray();
            }
            else if (supported.TryGetValue(eventClass, out var available))
            {
                columns = desired.Where(available.Contains).ToArray();
            }
            else if (eventClass == TraceEventClass.QueryEnd)
            {
                columns = desired;
            }
            else
            {
                continue;
            }

            // EventClass is implicit in every trace event; an event with nothing else is useless.
            if (columns.Any(c => c != TraceColumn.EventClass))
                result.Add((eventClass, columns));
        }
        return result;
    }

    /// <summary>
    /// Server-side trace filter: events from our query's session <em>or</em> our unique application
    /// name. The application name catches events whose SessionID the service rewrites; the columns
    /// must exist on at least one subscribed event (QueryEnd/DiscoverBegin carry both).
    /// </summary>
    internal static string BuildFilterXml(string sessionId, string applicationName)
        => $"<Or xmlns=\"{EngineNamespace}\">" +
           $"<Equal><ColumnID>{(int)TraceColumn.SessionID}</ColumnID><Value>{SecurityElement.Escape(sessionId)}</Value></Equal>" +
           $"<Equal><ColumnID>{(int)TraceColumn.ApplicationName}</ColumnID><Value>{SecurityElement.Escape(applicationName)}</Value></Equal>" +
           "</Or>";

    /// <summary>
    /// Client-side correlation: an event is ours when its SessionID or ApplicationName matches, or
    /// when it carries neither (a CLI runs one query at a time, and the server filter already
    /// scoped the trace). Only an event that names a <em>different</em> session and no matching
    /// application is dropped.
    /// </summary>
    internal static bool IsOwnEvent(string ownSessionId, string ownApplicationName, string? eventSessionId, string? eventApplicationName)
    {
        if (!string.IsNullOrEmpty(eventApplicationName) && ownApplicationName.Length > 0
            && string.Equals(eventApplicationName, ownApplicationName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrEmpty(eventSessionId) || ownSessionId.Length == 0)
            return true;
        return string.Equals(eventSessionId, ownSessionId, StringComparison.OrdinalIgnoreCase);
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

    private void OnEvent(object? sender, TraceEventArgs e)
    {
        try
        {
            // Any event proves the trace is live, even one we go on to drop.
            _firstEvent.Set();

            if (!IsOwnEvent(_sessionId, _applicationName, e.SessionID, ReadColumn(e, TraceColumn.ApplicationName)))
            {
                Interlocked.Increment(ref _foreignEvents);
                return;
            }

            // The heartbeat only signals start-up; it is not part of the query.
            if (e.EventClass == TraceEventClass.DiscoverBegin)
                return;

            var text = e.TextData;
            // Skip our own cache warm-up query so its QueryEnd doesn't complete the run early.
            if (!string.IsNullOrEmpty(text) && text.Contains(InternalMarker, StringComparison.Ordinal))
                return;

            var projected = new QueryTraceEvent(
                e.EventClass, e.EventSubclass,
                ReadLong(e, TraceColumn.Duration), ReadLong(e, TraceColumn.CpuTime), ReadLong(e, TraceColumn.IntegerData),
                text);
            WriteRaw(e.CurrentTime, projected);
            Process(projected);
        }
        catch
        {
            // A trace handler must never throw back into the AMO event loop.
        }
    }

    private static string? ReadColumn(TraceEventArgs e, TraceColumn column)
    {
        try { return e[column]; }
        catch { return null; }
    }

    /// <summary>
    /// Reads a numeric column without AMO's typed getters (<c>Duration</c>, <c>CpuTime</c>,
    /// <c>IntegerData</c>), which throw <see cref="ArgumentNullException"/> when the event carries no
    /// value for the column. Fabric XMLA sends such events (e.g. <c>VertiPaqSEQueryCacheMatch</c>
    /// has no duration), and the throw used to discard every event silently (issue #94).
    /// </summary>
    private static long ReadLong(TraceEventArgs e, TraceColumn column) => ParseLong(ReadColumn(e, column));

    internal static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private void WriteRaw(DateTime currentTime, QueryTraceEvent e)
    {
        if (_rawWriter is null) return;
        var line = new StringBuilder()
            .Append(currentTime.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
            .Append(e.EventClass).Append('/').Append(e.EventSubclass).Append('\t')
            .Append("dur=").Append(e.Duration).Append('\t')
            .Append("cpu=").Append(e.CpuTime).Append('\t')
            .Append("int=").Append(e.IntegerData);
        if (!string.IsNullOrEmpty(e.TextData))
            line.Append('\t').Append(e.TextData.Replace('\n', ' ').Replace('\r', ' ').Trim());
        lock (_rawWriter)
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
        finally
        {
            _firstEvent.Dispose();
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
