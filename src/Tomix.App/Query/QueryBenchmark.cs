using Tomix.Core.Models;

namespace Tomix.App.Query;

/// <summary>
/// Benchmark summary for a multi-run query (<c>--runs N</c>): the per-run timings plus aggregate
/// statistics over the total and storage-engine durations. Total uses the server-measured duration
/// when a trace was active, else the client wall-clock. Null when there is a single run.
/// </summary>
public sealed record QueryBenchmark(
    IReadOnlyList<QueryBenchmarkRun> Runs,
    QueryStat TotalStats,
    QueryStat? SeStats)
{
    public static QueryBenchmark? Compute(IReadOnlyList<QueryRun>? runs)
    {
        if (runs is null || runs.Count <= 1)
            return null;

        var benchRuns = runs
            .Select(r => new QueryBenchmarkRun(
                r.Index,
                r.Cold,
                r.Timings?.TotalMs ?? r.ClientMs,
                r.Timings?.StorageEngineMs))
            .ToList();

        var total = QueryStat.From(benchRuns.Select(r => (double)r.TotalMs));
        var seValues = benchRuns.Where(r => r.SeMs.HasValue).Select(r => (double)r.SeMs!.Value).ToList();
        var se = seValues.Count > 0 ? QueryStat.From(seValues) : null;
        return new QueryBenchmark(benchRuns, total, se);
    }
}

/// <param name="Index">1-based run number.</param>
/// <param name="Cold">Whether the cache was cleared before this run.</param>
/// <param name="TotalMs">Server total duration when traced, else client wall-clock.</param>
/// <param name="SeMs">Storage-engine duration when traced; null otherwise.</param>
public sealed record QueryBenchmarkRun(int Index, bool Cold, long TotalMs, long? SeMs);

/// <summary>Aggregate statistics over a set of durations (ms). StdDev is the population standard deviation.</summary>
public sealed record QueryStat(double Avg, double Min, double Max, double StdDev)
{
    public static QueryStat From(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
            return new QueryStat(0, 0, 0, 0);

        var avg = list.Average();
        var variance = list.Sum(v => (v - avg) * (v - avg)) / list.Count;
        return new QueryStat(avg, list.Min(), list.Max(), Math.Sqrt(variance));
    }
}
