using System.Globalization;
using Spectre.Console;
using Tomix.App.Query;
using Tomix.Core.Models;

namespace Tomix.Cli.Output;

/// <summary>
/// Renders a query rowset: a Spectre table with dynamic columns for humans, CSV for
/// <c>--output-format csv</c>, and json/csv file output for <c>-o/--output-file</c>.
/// The row-count/duration footer and truncation notice go to stderr so stdout stays
/// pipeable data.
/// </summary>
internal static class QueryResultRenderer
{
    public static void Render(QueryModelResult result, bool quiet)
    {
        if (result.Columns.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("The query returned no columns."));
            WriteFooter(result, quiet);
            return;
        }

        var table = Styling.NewTable(result.Columns.Select(c => c.Name).ToArray());
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (result.Columns[i].Type is "int64" or "double" or "decimal" or "dateTime")
                table.Columns[i].Alignment = Justify.Right;
        }

        foreach (var row in result.Rows)
            table.AddRow(row.Select(CellMarkup).ToArray());

        AnsiConsole.Write(table);
        WriteFooter(result, quiet);
    }

    public static void RenderCsv(QueryModelResult result)
        => CsvOutput.Write(
            result.Columns.Select(c => c.Name).ToList(),
            result.Rows.Select(NormalizeCsvRow));

    /// <summary>
    /// Writes the result to <paramref name="path"/> as <c>json</c> or <c>csv</c>
    /// (<paramref name="fileFormat"/> is already resolved by the command).
    /// </summary>
    public static void WriteFile(QueryModelResult result, string path, string fileFormat)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // BOM-less UTF-8: the file is data for downstream tools (jq, pandas), and a BOM
        // breaks strict JSON parsers.
        using var writer = new StreamWriter(full, append: false, new System.Text.UTF8Encoding(false));
        if (OutputFormats.IsJson(fileFormat))
            writer.WriteLine(JsonOutput.Serialize(result));
        else
            CsvOutput.Write(
                writer,
                result.Columns.Select(c => c.Name).ToList(),
                result.Rows.Select(NormalizeCsvRow));
    }

    public static void WriteFooter(QueryModelResult result, bool quiet)
    {
        if (quiet)
            return;

        var seconds = Styling.DurationSeconds(result.DurationMs / 1000.0);
        Console.Error.WriteLine($"{result.RowCount} row(s) ({seconds})");
        if (result.Truncated)
            Console.Error.WriteLine($"Output truncated at {result.RowCount} rows (--limit {result.RowCount}).");
    }

    /// <summary>
    /// Writes the server-timings summary (from <c>--trace</c>) to stderr as plain text so stdout
    /// stays pipeable data. No-op when timings are absent (tracing unavailable or not requested).
    /// </summary>
    public static void WriteTimings(QueryTimings? timings)
    {
        if (timings is null)
            return;

        var e = Console.Error;
        e.WriteLine();
        e.WriteLine("Server timings:");
        e.WriteLine($"  Total          {Ms(timings.TotalMs)}  (CPU {Ms(timings.TotalCpuMs)})");
        e.WriteLine($"  FE             {Ms(timings.FormulaEngineMs)}  ({Percent(timings.FormulaEngineMs, timings.TotalMs)})");
        e.WriteLine($"  SE             {Ms(timings.StorageEngineMs)}  ({Percent(timings.StorageEngineMs, timings.TotalMs)}, CPU {Ms(timings.StorageEngineCpuMs)})");
        e.WriteLine($"  SE queries     {timings.StorageEngineQueryCount}");
        e.WriteLine($"  SE cache hits  {timings.StorageEngineCacheHits}");
    }

    /// <summary>Writes the logical/physical query plans (from <c>--plan</c>) to stderr.</summary>
    public static void WritePlans(IReadOnlyList<QueryPlan>? plans)
    {
        if (plans is null || plans.Count == 0)
            return;

        var e = Console.Error;
        foreach (var plan in plans)
        {
            e.WriteLine();
            var kind = plan.Kind.Length > 0
                ? char.ToUpperInvariant(plan.Kind[0]) + plan.Kind[1..]
                : plan.Kind;
            e.WriteLine($"{kind} plan:");
            e.WriteLine(plan.Text.TrimEnd());
        }
    }

    /// <summary>Writes the multi-run benchmark summary (from <c>--runs</c>) to stderr.</summary>
    public static void WriteBenchmark(QueryBenchmark? benchmark)
    {
        if (benchmark is null)
            return;

        var e = Console.Error;
        e.WriteLine();
        e.WriteLine($"Benchmark ({benchmark.Runs.Count} runs):");
        foreach (var run in benchmark.Runs)
        {
            var cache = run.Cold ? "cold" : "warm";
            var se = run.SeMs.HasValue ? $"  SE {Ms(run.SeMs.Value)}" : "";
            e.WriteLine($"  run {run.Index} ({cache})  {Ms(run.TotalMs)}{se}");
        }

        e.WriteLine($"  Total  avg {Ms(benchmark.TotalStats.Avg)}  min {Ms(benchmark.TotalStats.Min)}  max {Ms(benchmark.TotalStats.Max)}  stddev {Ms(benchmark.TotalStats.StdDev)}");
        if (benchmark.SeStats is { } seStats)
            e.WriteLine($"  SE     avg {Ms(seStats.Avg)}  min {Ms(seStats.Min)}  max {Ms(seStats.Max)}  stddev {Ms(seStats.StdDev)}");
    }

    private static string Ms(long ms) => ms.ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    private static string Percent(long part, long total)
        => (total > 0 ? (100.0 * part / total) : 0).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string CellMarkup(object? value) => value switch
    {
        null => Styling.Muted("(blank)"),
        long l => Styling.Number(l),
        _ => Styling.MarkupEscape(FormatCell(value))
    };

    /// <summary>
    /// Invariant, deterministic cell text. DateTime uses ISO-8601 (seconds precision) so
    /// human and CSV output never depend on the machine locale.
    /// </summary>
    internal static string FormatCell(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// Pre-formats DateTime cells for <see cref="CsvOutput"/>, whose invariant
    /// <see cref="IFormattable"/> fallback would otherwise emit the locale-shaped
    /// general format for dates.
    /// </summary>
    private static IReadOnlyList<object?> NormalizeCsvRow(IReadOnlyList<object?> row)
        => row.Select(cell => cell is DateTime dt
            ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : cell).ToList();
}
