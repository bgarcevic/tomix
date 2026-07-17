using Tomix.App.Query;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class QueryBenchmarkTests
{
    [Fact]
    public void Compute_ReturnsNull_ForNullOrSingleRun()
    {
        Assert.Null(QueryBenchmark.Compute(null));
        Assert.Null(QueryBenchmark.Compute([new QueryRun(1, Cold: false, ClientMs: 42, Timings: null)]));
    }

    [Fact]
    public void Compute_UsesClientMs_AndOmitsSeStats_WhenUntraced()
    {
        var runs = new List<QueryRun>
        {
            new(1, Cold: false, ClientMs: 100, Timings: null),
            new(2, Cold: false, ClientMs: 60, Timings: null)
        };

        var benchmark = QueryBenchmark.Compute(runs)!;

        Assert.Equal(2, benchmark.Runs.Count);
        Assert.Null(benchmark.SeStats);
        Assert.Equal(80, benchmark.TotalStats.Avg);   // (100 + 60) / 2
        Assert.Equal(60, benchmark.TotalStats.Min);
        Assert.Equal(100, benchmark.TotalStats.Max);
        Assert.Null(benchmark.Runs[0].SeMs);
    }

    [Fact]
    public void Compute_PrefersServerTotal_AndAggregatesSe_WhenTraced()
    {
        var runs = new List<QueryRun>
        {
            new(1, Cold: true, ClientMs: 999, Timings: new QueryTimings(90, 0, 30, 60, 0, 2, 0)),
            new(2, Cold: true, ClientMs: 999, Timings: new QueryTimings(70, 0, 20, 40, 0, 2, 0))
        };

        var benchmark = QueryBenchmark.Compute(runs)!;

        // Total uses the server-measured duration (90/70), not the client wall-clock (999).
        Assert.Equal(80, benchmark.TotalStats.Avg);
        Assert.Equal(10, benchmark.TotalStats.StdDev);  // population stddev of {90,70}
        Assert.NotNull(benchmark.SeStats);
        Assert.Equal(50, benchmark.SeStats!.Avg);       // (60 + 40) / 2
        Assert.Equal(60, benchmark.Runs[0].SeMs);
    }

    [Fact]
    public void QueryStat_From_ComputesPopulationStdDev()
    {
        var stat = QueryStat.From([2, 4, 4, 4, 5, 5, 7, 9]);

        Assert.Equal(5, stat.Avg);
        Assert.Equal(2, stat.Min);
        Assert.Equal(9, stat.Max);
        Assert.Equal(2, stat.StdDev);  // known population stddev of this set
    }

    [Fact]
    public void QueryStat_From_HandlesEmpty()
    {
        var stat = QueryStat.From([]);

        Assert.Equal(0, stat.Avg);
        Assert.Equal(0, stat.StdDev);
    }
}
