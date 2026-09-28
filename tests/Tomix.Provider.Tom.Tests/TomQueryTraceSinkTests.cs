using Microsoft.AnalysisServices;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Drives <see cref="TomQueryTraceSink"/>'s aggregation with synthetic trace events (no live
/// server), the same testability split <see cref="RefreshTraceSink"/> uses. The live trace
/// creation against an XMLA endpoint is covered by manual QA.
/// </summary>
public sealed class TomQueryTraceSinkTests
{
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(1);

    private static QueryTraceEvent Scan(long dur, long cpu, TraceEventSubclass sub = TraceEventSubclass.VertiPaqScan)
        => new(TraceEventClass.VertiPaqSEQueryEnd, sub, dur, cpu, 0, null);

    private static QueryTraceEvent QueryEnd(long dur, long cpu)
        => new(TraceEventClass.QueryEnd, TraceEventSubclass.VertiPaqScan, dur, cpu, 0, null);

    [Fact]
    public void Process_AggregatesStorageEngineScansAndCacheHits()
    {
        var sink = new TomQueryTraceSink();
        sink.StartRun();

        sink.Process(Scan(dur: 50, cpu: 40));
        sink.Process(Scan(dur: 30, cpu: 20));
        sink.Process(new QueryTraceEvent(TraceEventClass.VertiPaqSEQueryCacheMatch, TraceEventSubclass.VertiPaqCacheExactMatch, 0, 0, 0, null));
        sink.Process(QueryEnd(dur: 100, cpu: 200));

        var timings = sink.WaitForRun(Ready)!;
        Assert.Equal(100, timings.TotalMs);
        Assert.Equal(200, timings.TotalCpuMs);
        Assert.Equal(80, timings.StorageEngineMs);       // 50 + 30
        Assert.Equal(60, timings.StorageEngineCpuMs);    // 40 + 20
        Assert.Equal(20, timings.FormulaEngineMs);       // max(100 - 80, 0)
        Assert.Equal(2, timings.StorageEngineQueryCount);
        Assert.Equal(1, timings.StorageEngineCacheHits);
    }

    [Fact]
    public void Process_IgnoresBatchScans_ToAvoidDoubleCounting()
    {
        var sink = new TomQueryTraceSink();
        sink.StartRun();

        // A batch (outer) scan wraps leaf scans; counting it too would double the SE time.
        sink.Process(Scan(dur: 100, cpu: 90, sub: TraceEventSubclass.BatchVertiPaqScan));
        sink.Process(Scan(dur: 40, cpu: 30));
        sink.Process(QueryEnd(dur: 50, cpu: 60));

        var timings = sink.WaitForRun(Ready)!;
        Assert.Equal(40, timings.StorageEngineMs);
        Assert.Equal(1, timings.StorageEngineQueryCount);
        Assert.Equal(10, timings.FormulaEngineMs);       // max(50 - 40, 0)
    }

    [Fact]
    public void Process_DirectQueryEnd_CountsAsStorageEngine()
    {
        var sink = new TomQueryTraceSink();
        sink.StartRun();

        sink.Process(new QueryTraceEvent(TraceEventClass.DirectQueryEnd, TraceEventSubclass.NotAvailable, 25, 25, 0, null));
        sink.Process(QueryEnd(dur: 40, cpu: 40));

        var timings = sink.WaitForRun(Ready)!;
        Assert.Equal(25, timings.StorageEngineMs);
        Assert.Equal(1, timings.StorageEngineQueryCount);
        Assert.Equal(15, timings.FormulaEngineMs);
    }

    [Fact]
    public void StartRun_ResetsAccumulatorsBetweenRuns()
    {
        var sink = new TomQueryTraceSink();

        sink.StartRun();
        sink.Process(Scan(dur: 50, cpu: 40));
        sink.Process(QueryEnd(dur: 100, cpu: 100));
        Assert.Equal(50, sink.WaitForRun(Ready)!.StorageEngineMs);

        sink.StartRun();
        sink.Process(QueryEnd(dur: 30, cpu: 30));
        var second = sink.WaitForRun(Ready)!;
        Assert.Equal(0, second.StorageEngineMs);         // prior run's scans cleared
        Assert.Equal(30, second.FormulaEngineMs);
        Assert.Equal(0, second.StorageEngineQueryCount);
    }

    // ── Trace definition (issue #94: Fabric rejected the trace, then delivered no events) ──

    private static string Event(TraceEventClass id, params TraceColumn[] columns)
        => $"<EVENT><ID>{(int)id}</ID><EVENTCOLUMNLIST>" +
           string.Concat(columns.Select(c => $"<EVENTCOLUMN><ID>{(int)c}</ID></EVENTCOLUMN>")) +
           "</EVENTCOLUMNLIST></EVENT>";

    // Shaped like a DISCOVER_TRACE_EVENT_CATEGORIES row; DirectQueryEnd lacks EventSubclass, as on Fabric.
    private static readonly string Categories =
        "<EVENTCATEGORY><NAME>Queries Events</NAME><EVENTLIST>" +
        Event(TraceEventClass.QueryEnd, TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.Duration,
            TraceColumn.ApplicationName, TraceColumn.SessionID) +
        Event(TraceEventClass.DirectQueryEnd, TraceColumn.EventClass, TraceColumn.Duration, TraceColumn.SessionID) +
        "</EVENTLIST></EVENTCATEGORY>";

    [Fact]
    public void ParseSupportedColumns_ReadsEventAndColumnIds_AndSkipsMalformedDocuments()
    {
        var supported = TomQueryTraceSink.ParseSupportedColumns([Categories, "<not-xml"]);

        Assert.Equal(2, supported.Count);
        Assert.Equal(
            new[] { TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.Duration, TraceColumn.ApplicationName, TraceColumn.SessionID }.Order(),
            supported[TraceEventClass.QueryEnd].Order());
        Assert.DoesNotContain(TraceColumn.EventSubclass, supported[TraceEventClass.DirectQueryEnd]);
    }

    [Fact]
    public void BuildEventColumns_WithDiscoveredColumns_NarrowsEachEventToWhatTheServerSupports()
    {
        var supported = TomQueryTraceSink.ParseSupportedColumns([Categories]);

        var events = TomQueryTraceSink.BuildEventColumns(supported).ToDictionary(e => e.Event, e => e.Columns);

        // The exact pair Fabric rejected: DirectQueryEnd (99) with EventSubclass (1).
        Assert.DoesNotContain(TraceColumn.EventSubclass, events[TraceEventClass.DirectQueryEnd]);
        Assert.Equal(
            new[] { TraceColumn.EventClass, TraceColumn.EventSubclass, TraceColumn.Duration, TraceColumn.SessionID, TraceColumn.ApplicationName },
            events[TraceEventClass.QueryEnd]);
        // Events the server does not list are not subscribed (it would reject the trace).
        Assert.DoesNotContain(TraceEventClass.VertiPaqSEQueryEnd, events.Keys);
    }

    [Fact]
    public void BuildEventColumns_WithoutDiscovery_DropsKnownRejectedColumns()
    {
        var events = TomQueryTraceSink.BuildEventColumns(supported: null).ToDictionary(e => e.Event, e => e.Columns);

        Assert.DoesNotContain(TraceColumn.EventSubclass, events[TraceEventClass.DirectQueryEnd]);
        Assert.DoesNotContain(TraceColumn.ApplicationName, events[TraceEventClass.VertiPaqSEQueryEnd]);
        Assert.Contains(TraceColumn.ApplicationName, events[TraceEventClass.QueryEnd]);
        Assert.Contains(TraceEventClass.DiscoverBegin, events.Keys);   // start-up heartbeat
    }

    [Fact]
    public void BuildFilterXml_OrsSessionIdAndApplicationName_Escaped()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(TomQueryTraceSink.BuildFilterXml("S<1>", "tomix-query-&"));

        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("e", "http://schemas.microsoft.com/analysisservices/2003/engine");
        var equals = doc.SelectNodes("/e:Or/e:Equal", ns)!;
        Assert.Equal(2, equals.Count);
        Assert.Equal(((int)TraceColumn.SessionID).ToString(), equals[0]!["ColumnID"]!.InnerText);
        Assert.Equal("S<1>", equals[0]!["Value"]!.InnerText);
        Assert.Equal(((int)TraceColumn.ApplicationName).ToString(), equals[1]!["ColumnID"]!.InnerText);
        Assert.Equal("tomix-query-&", equals[1]!["Value"]!.InnerText);
    }

    [Theory]
    [InlineData("S1", "app", true)]      // same session
    [InlineData("S2", "app", true)]      // session rewritten by the service, application matches
    [InlineData("S2", "APP", true)]      // application match is case-insensitive
    [InlineData(null, null, true)]       // event carries neither column
    [InlineData("S2", null, false)]      // another session, no application to vouch for it
    [InlineData("S2", "other", false)]   // another session and another application
    public void IsOwnEvent_MatchesOnSessionOrApplication(string? eventSession, string? eventApp, bool expected)
        => Assert.Equal(expected, TomQueryTraceSink.IsOwnEvent("S1", "app", eventSession, eventApp));

    // Fabric sends events with empty numeric columns (QueryEnd has no IntegerData, cache matches no
    // Duration); AMO's typed getters throw on those, which silently discarded every event.
    [Theory]
    [InlineData("94", 94)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("n/a", 0)]
    public void ParseLong_TreatsMissingOrNonNumericColumnsAsZero(string? raw, long expected)
        => Assert.Equal(expected, TomQueryTraceSink.ParseLong(raw));
}
