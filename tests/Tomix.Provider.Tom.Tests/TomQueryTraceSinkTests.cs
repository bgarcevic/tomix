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
    public void Process_CapturesLogicalAndPhysicalPlans()
    {
        var sink = new TomQueryTraceSink();
        sink.StartRun();

        sink.Process(new QueryTraceEvent(TraceEventClass.DAXQueryPlan, TraceEventSubclass.DAXVertiPaqLogicalPlan, 0, 0, 0, "LOGICAL-TREE"));
        sink.Process(new QueryTraceEvent(TraceEventClass.DAXQueryPlan, TraceEventSubclass.DAXVertiPaqPhysicalPlan, 0, 0, 0, "PHYSICAL-TREE"));

        var plans = sink.BuildPlans()!;
        Assert.Collection(plans,
            p => { Assert.Equal("logical", p.Kind); Assert.Equal("LOGICAL-TREE", p.Text); },
            p => { Assert.Equal("physical", p.Kind); Assert.Equal("PHYSICAL-TREE", p.Text); });
    }

    [Fact]
    public void BuildPlans_ReturnsNull_WhenNoPlanEvents()
    {
        var sink = new TomQueryTraceSink();
        sink.StartRun();
        sink.Process(QueryEnd(dur: 10, cpu: 10));

        Assert.Null(sink.BuildPlans());
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
}
