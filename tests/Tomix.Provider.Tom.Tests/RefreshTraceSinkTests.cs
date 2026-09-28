using Microsoft.AnalysisServices;
using Tomix.Core.Models;
using AsTraceEventClass = Microsoft.AnalysisServices.TraceEventClass;
using AsTraceEventSubclass = Microsoft.AnalysisServices.TraceEventSubclass;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Synthetic event-stream tests for <see cref="RefreshTraceSink"/>. The patterns are lifted
/// directly from a real Power BI Service trace dump (refresh of a 21-table model) — each test
/// replays a representative event sequence without needing a live server, thanks to the
/// AMO-free <see cref="RefreshTraceEvent"/> projection.
/// </summary>
public sealed class RefreshTraceSinkTests
{
    private static readonly DateTime T0 = new(2026, 6, 18, 22, 10, 46, DateTimeKind.Utc);

    private static RefreshTraceEvent Ev(
        AsTraceEventClass eventClass,
        AsTraceEventSubclass subclass,
        long duration = 0,
        long integerData = 0,
        string objectName = "",
        string? textData = null,
        string? error = null,
        DateTime? startTime = null,
        string? objectPath = null,
        int objectType = 0)
        => new(startTime ?? T0, eventClass, subclass, duration, integerData, objectName, textData, error, objectPath, objectType);

    // Power BI / Fabric events: attributed by ObjectPath + ObjectType.

    private const string Db = "00000000-0000-4000-8000-000000000001";

    private static RefreshTraceEvent Partition(
        AsTraceEventClass eventClass, AsTraceEventSubclass subclass, string table, string partition,
        long duration = 0, long rows = 0, DateTime? start = null)
        => Ev(eventClass, subclass, duration, rows, objectName: partition, startTime: start,
            objectPath: $"{Db}.Model.{table}.{partition}", objectType: RefreshTraceSink.ObjectTypePartition);

    private static RefreshTraceEvent Child(string table, string child, int objectType, long duration)
        => Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration, objectName: child,
            objectPath: $"{Db}.Model.{table}.{child}", objectType: objectType);

    private static RefreshTraceSink NewSink(IReadOnlyList<string> knownTables, IProgress<RefreshProgress>? progress = null)
        => new(knownTables, progress);

    /// <summary>
    /// Posteringer is the biggest SQL-backed table. Captured event sequence: ExecuteSql(3049) →
    /// ReadData(14053, 2042139 rows) → Process(15038) → TabularRefresh partition end(18118).
    /// Expected per the reference CLI's arithmetic: Query=3049, Read=14053, Rows=2042139,
    /// Total=Query+Read=17102.
    /// </summary>
    [Fact]
    public void SqlBackedTable_ExecuteSql_ReadData_TabularRefresh_Produces_Query_Read_Rows_Total()
    {
        var sink = NewSink(["Posteringer"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 3049, objectName: "Posteringer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 14053, integerData: 2042139, objectName: "Posteringer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.Process, duration: 15038, objectName: "Posteringer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 18118, objectName: "Posteringer"));

        var result = Assert.Single(sink.BuildTableResults());
        Assert.Equal("Posteringer", result.Table);
        Assert.Equal(2_042_139, result.Rows);
        Assert.Equal(3049, result.QueryMs);
        Assert.Equal(14053, result.ReadMs);
        Assert.Equal(17102, result.TotalMs); // Query + Read
    }

    /// <summary>
    /// PnL kategori is a calculated table with a fast M evaluation. ReadData fires with 0 duration
    /// but carries the 8-row count. Total = Query + Read = 109 + 0 = 109.
    /// </summary>
    [Fact]
    public void CalculatedTable_WithZeroReadDuration_Captures_RowCount_ViaReadData()
    {
        var sink = NewSink(["PnL kategori"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 109, objectName: "PnL kategori"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 0, integerData: 8, objectName: "PnL kategori"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 141, objectName: "PnL kategori"));

        var result = Assert.Single(sink.BuildTableResults());
        Assert.Equal("PnL kategori", result.Table);
        Assert.Equal(8, result.Rows);
        Assert.Equal(109, result.QueryMs);
        Assert.Equal(0, result.ReadMs);
        Assert.Equal(109, result.TotalMs);
    }

    /// <summary>
    /// Datoer is a calculated table that does real M work (12,053 rows). ExecuteSql=110,
    /// ReadData=173, sum=283=Total.
    /// </summary>
    [Fact]
    public void CalculatedTable_WithMRead_Captures_BothQueryAndRead()
    {
        var sink = NewSink(["Datoer"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 110, objectName: "Datoer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 173, integerData: 12053, objectName: "Datoer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 314, objectName: "Datoer"));

        var result = Assert.Single(sink.BuildTableResults());
        Assert.Equal(12_053, result.Rows);
        Assert.Equal(110, result.QueryMs);
        Assert.Equal(173, result.ReadMs);
        Assert.Equal(283, result.TotalMs);
    }

    /// <summary>
    /// TabularRefresh events fire with child names (hierarchies, columns, relationships) and
    /// Duration=0. They must NOT overwrite the partition-level total. The partition event has
    /// ObjectName == table name; child events have ObjectName == child name (resolved via TextData).
    /// </summary>
    [Fact]
    public void ChildTabularRefreshEvents_DoNot_Overwrite_PartitionTotals()
    {
        var sink = NewSink(["Vagttyper"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 766, objectName: "Vagttyper"));
        // Hierarchy/column events — same table resolved via TextData, Duration=0, different ObjectName.
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 0,
            objectName: "RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61",
            textData: "Behandlingen af hierarkiet 'RowNumber-…' i tabellen 'Vagttyper' er afsluttet."));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 16,
            objectName: "År Måned Nummer",
            textData: "Behandlingen af hierarkiet 'År Måned Nummer' i tabellen 'Vagttyper' er afsluttet."));

        var result = Assert.Single(sink.BuildTableResults());
        // No ExecuteSql/ReadData fired, so Total falls back to the partition-level TabularRefresh
        // duration. The child events must not overwrite it.
        Assert.Equal(766, result.TotalMs);
    }

    /// <summary>
    /// TabularCommit marks the refresh transaction as committed. Every tracked table should be
    /// marked Completed (so the live display knows the refresh is done) regardless of whether
    /// individual TabularRefresh partition events fired.
    /// </summary>
    [Fact]
    public void TabularCommit_Marks_All_Tables_Completed()
    {
        var reports = new List<RefreshProgress>();
        var progress = new SynchronousProgress(reports.Add);
        var sink = new RefreshTraceSink(["TableA", "TableB"], progress);

        // Each table gets only an ExecuteSql event — no TabularRefresh partition end.
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 100, objectName: "TableA"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 200, objectName: "TableB"));

        // The final commit event.
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularCommit, duration: 50));

        var results = sink.BuildTableResults().ToDictionary(t => t.Table, StringComparer.Ordinal);
        Assert.True(results["TableA"].TotalMs > 0);
        Assert.True(results["TableB"].TotalMs > 0);
        // The commit reports a model-level progress snapshot (empty table name).
        Assert.Contains(reports, p => p.Phase == "commit" && p.Completed);
    }

    /// <summary>
    /// Power BI Service never emits ProgressReportCurrent, but on-prem AS / Fabric might. When
    /// one fires with IntegerData > 0, the live row counter should update mid-stream so the
    /// spinner ticks during long reads.
    /// </summary>
    [Fact]
    public void ProgressReportCurrent_WithIntegerData_Updates_RowCount_Live()
    {
        var reports = new List<RefreshProgress>();
        var progress = new SynchronousProgress(reports.Add);
        var sinkWithProgress = new RefreshTraceSink(["BigTable"], progress);

        // Three Current events as rows stream in.
        sinkWithProgress.Process(Ev(AsTraceEventClass.ProgressReportCurrent, AsTraceEventSubclass.ExecuteSql, integerData: 500, objectName: "BigTable"));
        sinkWithProgress.Process(Ev(AsTraceEventClass.ProgressReportCurrent, AsTraceEventSubclass.ExecuteSql, integerData: 1500, objectName: "BigTable"));
        // End event with the final count.
        sinkWithProgress.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 5000, integerData: 2000, objectName: "BigTable"));

        var result = Assert.Single(sinkWithProgress.BuildTableResults());
        Assert.Equal(2000, result.Rows);

        // Live snapshots should have reported the intermediate counts.
        Assert.Contains(reports, p => p is { Table: "BigTable", RowsRead: 500 });
        Assert.Contains(reports, p => p is { Table: "BigTable", RowsRead: 1500 });
    }

    /// <summary>
    /// Regression test for the per-table error capture that enriches refresh failures with the
    /// failing table name (added in an earlier turn). A ProgressReportError with ObjectName set
    /// to a known table should surface in BuildTableErrors under that table's key.
    /// </summary>
    [Fact]
    public void ProgressReportError_Captures_PerTable_Error_Messages()
    {
        var sink = NewSink(["Posteringer", "Datoer"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportError, AsTraceEventSubclass.TabularRefresh,
            objectName: "Datoer",
            textData: "Processing of partition 'Datoer' failed.",
            error: "Kolonnen '<oii>Dag Type</oii>' findes ikke i rækkesættet."));

        var errors = sink.BuildTableErrors();
        Assert.True(errors.ContainsKey("Datoer"));
        Assert.Contains(errors["Datoer"], m => m.Contains("Dag Type"));
    }

    /// <summary>
    /// ProgressReportError whose ObjectName isn't a known table falls back to TextData matching.
    /// When TextData also fails to match, the error goes under the empty-string key so it still
    /// appears in the failure message under "(unknown table)".
    /// </summary>
    [Fact]
    public void ProgressReportError_WithUnresolvableTable_Goes_Under_EmptyKey()
    {
        var sink = NewSink(["TableA"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportError, AsTraceEventSubclass.TabularRefresh,
            objectName: "SomeChildObject",
            textData: "no table name here",
            error: "mystery failure"));

        var errors = sink.BuildTableErrors();
        Assert.True(errors.ContainsKey(""));
        Assert.Contains("mystery failure", errors[""]);
    }

    /// <summary>
    /// Defensive: if neither ExecuteSql nor ReadData fires for a table (never observed in real
    /// traces), Total falls back to the TabularRefresh partition-end duration so the summary
    /// still shows a non-zero number.
    /// </summary>
    [Fact]
    public void Total_FallsBack_To_TabularRefreshDuration_When_No_ExecuteSql_Or_ReadData()
    {
        var sink = NewSink(["EdgeTable"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 9999, objectName: "EdgeTable"));

        var result = Assert.Single(sink.BuildTableResults());
        Assert.Equal(9999, result.TotalMs);
        Assert.Equal(0, result.QueryMs);
        Assert.Equal(0, result.ReadMs);
    }

    /// <summary>
    /// CompressSegment / VertiPaq / AnalyzeEncodeData / TabularSequencePoint events are noise for
    /// Query/Read/Total purposes. They fire frequently (229 CompressSegment events in the real
    /// trace) and must not produce spurious table entries or overwrite accumulator state.
    /// </summary>
    [Fact]
    public void CompressSegment_And_Other_NoiseSubclasses_Are_Ignored()
    {
        var sink = NewSink(["Posteringer"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.CompressSegment, duration: 31,
            objectName: "sk_selskab",
            textData: "Finished compressing segment 0 of column 'sk_selskab' for the 'Posteringer' table."));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularSequencePoint, duration: 5));

        // No real phase events fired, so BuildTableResults is empty — the noise didn't create
        // a phantom Posteringer entry.
        Assert.Empty(sink.BuildTableResults());
    }

    /// <summary>
    /// The full refresh of the captured model touched 21 tables. This is a regression test that
    /// replays a representative slice (3 tables with full sequences) to ensure ordering, sorting,
    /// and aggregation behave end-to-end.
    /// </summary>
    [Fact]
    public void MultiTable_Refresh_Produces_Ordered_Results()
    {
        var sink = NewSink(["Posteringer", "PnL kategori", "Datoer"]);

        // PnL kategori (calculated, smallest Total).
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 109, objectName: "PnL kategori"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 0, integerData: 8, objectName: "PnL kategori"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 141, objectName: "PnL kategori"));

        // Datoer (calculated with read).
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 110, objectName: "Datoer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 173, integerData: 12053, objectName: "Datoer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 314, objectName: "Datoer"));

        // Posteringer (SQL-backed, biggest Total).
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, duration: 3049, objectName: "Posteringer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, duration: 14053, integerData: 2042139, objectName: "Posteringer"));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 18118, objectName: "Posteringer"));

        var results = sink.BuildTableResults();
        Assert.Equal(3, results.Count);
        // Ordered by ascending TotalMs.
        Assert.Equal("PnL kategori", results[0].Table);
        Assert.Equal(109, results[0].TotalMs);
        Assert.Equal("Datoer", results[1].Table);
        Assert.Equal(283, results[1].TotalMs);
        Assert.Equal("Posteringer", results[2].Table);
        Assert.Equal(17102, results[2].TotalMs);
    }

    /// <summary>
    /// The --trace flag dumps every event with its raw fields. The format must stay stable so
    /// external tools and log reviews can parse it. This test pins the column layout.
    /// </summary>
    [Fact]
    public void TraceWriter_Emits_Stable_Tsv_Format()
    {
        var sw = new System.IO.StringWriter();
        var sink = new RefreshTraceSink(["T"], traceWriter: sw);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql,
            duration: 516,
            integerData: 0,
            objectName: "T",
            textData: "let __AS_Query__ = T in __AS_Query__"));

        var line = sw.ToString().TrimEnd();
        var cols = line.Split('\t');
        Assert.True(cols.Length >= 6);
        Assert.Equal("ProgressReportEnd/ExecuteSql", cols[1]);
        Assert.Equal("dur=516", cols[2]);
        Assert.Equal("int=0", cols[3]);
        Assert.Equal("obj=T", cols[4]);
        Assert.Contains("let __AS_Query__", cols[5]);
    }

    /// <summary>
    /// Regression: a table with several partitions reported only the last partition's rows and
    /// timings, because each partition overwrote the table's accumulator (two 2-row partitions
    /// showed as 2 rows). Rows and durations now sum across partitions, which are also listed.
    /// </summary>
    [Fact]
    public void MultiPartitionTable_SumsPartitions_AndListsThem()
    {
        var sink = NewSink(["Events"]);

        foreach (var (partition, query, read) in new[] { ("2026Q308", 10L, 20L), ("2026Q309", 30L, 40L) })
        {
            sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, "Events", partition, duration: query));
            sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ReadData, "Events", partition, duration: read, rows: 2));
            sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Events", partition, duration: 99));
        }

        var table = Assert.Single(sink.BuildTableResults());
        Assert.Equal(4, table.Rows);
        Assert.Equal(40, table.QueryMs);
        Assert.Equal(60, table.ReadMs);
        Assert.Equal(100, table.TotalMs);
        Assert.Collection(table.Partitions!,
            p => { Assert.Equal("2026Q308", p.Partition); Assert.Equal(2, p.Rows); Assert.Equal(30, p.TotalMs); },
            p => { Assert.Equal("2026Q309", p.Partition); Assert.Equal(2, p.Rows); Assert.Equal(70, p.TotalMs); });
    }

    /// <summary>
    /// Begin events (which carry no duration) used to be dropped, so the live display only ever
    /// saw a table once it finished. They now drive the phase, and a table only reports
    /// completed once every partition it began has ended.
    /// </summary>
    [Fact]
    public void BeginEvents_DriveLivePhase_AndTableCompletesAfterItsLastPartition()
    {
        var reports = new List<RefreshProgress>();
        var sink = new RefreshTraceSink(["Metrics"], new SynchronousProgress(reports.Add));

        sink.Process(Partition(AsTraceEventClass.ProgressReportBegin, AsTraceEventSubclass.TabularRefresh, "Metrics", "H1"));
        sink.Process(Partition(AsTraceEventClass.ProgressReportBegin, AsTraceEventSubclass.TabularRefresh, "Metrics", "H2"));
        sink.Process(Partition(AsTraceEventClass.ProgressReportBegin, AsTraceEventSubclass.ExecuteSql, "Metrics", "H1"));
        Assert.Equal(("query", "H1", false), (reports[^1].Phase, reports[^1].Partition, reports[^1].Completed));

        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, "Metrics", "H1", duration: 5));
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Metrics", "H1", duration: 5));
        Assert.False(reports[^1].Completed);             // H2 still running

        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Metrics", "H2", duration: 5));
        Assert.True(reports[^1].Completed);
        Assert.Equal("Metrics", reports[^1].Table);
    }

    /// <summary>
    /// A sub-step whose End never arrives must not keep the table "in progress" once its
    /// partition has finished — that pinned tables on the live status for the whole refresh.
    /// </summary>
    [Fact]
    public void PartitionEnd_ClearsUnpairedSubSteps_SoTheTableCompletes()
    {
        var reports = new List<RefreshProgress>();
        var sink = new RefreshTraceSink(["Sales"], new SynchronousProgress(reports.Add));

        sink.Process(Partition(AsTraceEventClass.ProgressReportBegin, AsTraceEventSubclass.TabularRefresh, "Sales", "Sales"));
        sink.Process(Partition(AsTraceEventClass.ProgressReportBegin, AsTraceEventSubclass.ExecuteSql, "Sales", "Sales"));
        // No ExecuteSql End.
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Sales", "Sales", duration: 5));

        Assert.True(reports[^1].Completed);
    }

    /// <summary>
    /// Hierarchy and calculated-column TabularRefresh events are post-load work: they add to the
    /// table's ProcessMs and their phases, and never touch the partition's load numbers.
    /// </summary>
    [Fact]
    public void PostLoadEvents_FeedProcessMs_AndPhases_NotLoadTotals()
    {
        var sink = NewSink(["Customer"]);

        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.ExecuteSql, "Customer", "Customer", duration: 16));
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Customer", "Customer", duration: 16));
        sink.Process(Child("Customer", "Country", RefreshTraceSink.ObjectTypeAttributeHierarchy, duration: 7));
        sink.Process(Child("Customer", "Geography", RefreshTraceSink.ObjectTypeUserHierarchy, duration: 3));
        sink.Process(Child("Customer", "NameLength", RefreshTraceSink.ObjectTypeCalculatedColumn, duration: 5));

        var table = Assert.Single(sink.BuildTableResults());
        Assert.Equal(16, table.TotalMs);
        Assert.Equal(15, table.ProcessMs);
        Assert.Equal("Customer", Assert.Single(table.Partitions!).Partition);

        var phases = sink.BuildPhases()!.ToDictionary(p => p.Phase);
        Assert.Equal(2, phases["hierarchies"].Count);
        Assert.Equal(1, phases["calculatedColumns"].Count);
    }

    [Fact]
    public void ModelLevelEvents_BecomePhases_InProcessingOrder()
    {
        var sink = NewSink(["Sales"]);

        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularCommit, duration: 219));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, duration: 4,
            objectName: "Sales_Customer", objectPath: $"{Db}.Model.Sales_Customer", objectType: RefreshTraceSink.ObjectTypeRelationship));
        sink.Process(Ev(AsTraceEventClass.ExecuteMdxScriptEnd, AsTraceEventSubclass.MdxScript, duration: 12));
        sink.Process(Ev(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularSequencePoint, duration: 781));
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "Sales", "Sales", duration: 313));

        Assert.Equal(
            ["load", "relationships", "calculationScript", "sequencePoint", "commit"],
            sink.BuildPhases()!.Select(p => p.Phase));
        // A relationship is not a table.
        Assert.Equal("Sales", Assert.Single(sink.BuildTableResults()).Table);
    }

    /// <summary>
    /// Phase time is wall-clock: partitions loading in parallel overlap, and summing their
    /// durations would overstate the phase.
    /// </summary>
    [Fact]
    public void PhaseDuration_MergesOverlappingIntervals()
    {
        var sink = NewSink(["A", "B", "C"]);

        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "A", "A", duration: 100, start: T0));
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "B", "B", duration: 100, start: T0.AddMilliseconds(50)));
        sink.Process(Partition(AsTraceEventClass.ProgressReportEnd, AsTraceEventSubclass.TabularRefresh, "C", "C", duration: 10, start: T0.AddMilliseconds(500)));

        var load = Assert.Single(sink.BuildPhases()!);
        Assert.Equal(3, load.Count);
        Assert.Equal(160, load.DurationMs);             // [0,150] + [500,510]
    }

    [Theory]
    [InlineData("db.Model.Sales.Sales-2024", "Sales", "Sales-2024")]
    [InlineData("db.Model.Sales.v2.Sales-2024", "Sales.v2", "Sales-2024")]   // dotted table name
    [InlineData("db.Model.Sales Detail.P1", "Sales Detail", "P1")]          // longest match wins
    [InlineData("db.Model.Sales", "Sales", null)]
    [InlineData("db.Model.Sales_Customer", null, null)]                     // relationship
    [InlineData("db", null, null)]
    public void ResolvePath_MatchesKnownTables(string path, string? table, string? child)
        => Assert.Equal((table, child), NewSink(["Sales", "Sales.v2", "Sales Detail"]).ResolvePath(path));

    [Fact]
    public void WaitForCommit_ReturnsImmediately_WithoutAnAttachedTrace()
        => Assert.False(NewSink(["T"]).WaitForCommit(TimeSpan.FromSeconds(30)));

    [Theory]
    [InlineData("94", 94)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseLong_TreatsMissingColumnsAsZero(string? raw, long expected)
        => Assert.Equal(expected, RefreshTraceSink.ParseLong(raw));

    /// <summary>
    /// Minimal synchronous IProgress adapter mirroring <c>RefreshLiveDisplay.SynchronousProgress</c>
    /// in the CLI: calls the handler directly on the reporting thread so tests see reports in order.
    /// </summary>
    private sealed class SynchronousProgress : IProgress<RefreshProgress>
    {
        private readonly Action<RefreshProgress> _onReport;
        public SynchronousProgress(Action<RefreshProgress> onReport) => _onReport = onReport;
        public void Report(RefreshProgress value) => _onReport(value);
    }
}
