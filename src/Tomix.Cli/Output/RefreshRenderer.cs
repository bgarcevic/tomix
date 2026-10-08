using System.Globalization;
using Spectre.Console;
using Tomix.App.Refresh;
using RefreshPartitionResult = Tomix.Core.Models.RefreshPartitionResult;
using RefreshPhaseResult = Tomix.Core.Models.RefreshPhaseResult;
using RefreshTableResult = Tomix.Core.Models.RefreshTableResult;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering for the <c>refresh</c> command: header + per-table statistics table with partition
/// sub-rows and a model-phase table (text), CSV rows, and the preview TMSL script
/// pretty-print.
/// </summary>
internal static class RefreshRenderer
{
    public static void Render(RefreshModelResult result)
    {
        if (result.PolicyPreview is { } preview)
        {
            AnsiConsole.MarkupLine(Styling.Guidance(
                $"Would apply refresh policy: {preview.Table} on {result.Database} (effective {preview.EffectiveDate:yyyy-MM-dd}). No data loading. Expired partitions may be removed."));
            StdErr.MarkupLine(Styling.Guidance("Preview: no policy was applied. Partition changes are determined on execution."));
            return;
        }
        if (result.PolicyApplication is { } policy)
        {
            AnsiConsole.MarkupLine(Styling.Success($"Applied refresh policy: {policy.Table} on {policy.Database} (effective {policy.EffectiveDate:yyyy-MM-dd})"));
            foreach (var operation in policy.Operations)
                AnsiConsole.MarkupLine(Styling.Muted(operation));
            if (policy.Operations.Count == 0)
                AnsiConsole.MarkupLine(Styling.Muted("Partitions already match the policy; no changes."));
            StdErr.MarkupLine(Styling.Guidance("No data was loaded. Run 'tx refresh --table <table>' to load data."));
            return;
        }

        var database = string.IsNullOrWhiteSpace(result.Database) ? "<model>" : result.Database;
        var server = string.IsNullOrWhiteSpace(result.Server) ? "<endpoint>" : result.Server;
        var seconds = Styling.DurationSeconds(result.DurationMs / 1000.0);

        var header =
            $"{Styling.Success("Refreshed")} " +
            $"{Styling.Bold(database)} " +
            $"{Styling.Muted("on")} " +
            $"{Styling.Path(server)} " +
            Styling.Muted($"({seconds})");
        // The header is commentary: stderr keeps `tx refresh > file` down to the statistics.
        var err = StdErr.Console();
        err.MarkupLine(header);
        err.WriteLine();

        if (result.Tables.Count == 0)
        {
            err.MarkupLine(Styling.Muted("No per-table statistics available. Use without --no-progress to capture XMLA trace events."));
            return;
        }

        var table = Styling.NewTable("Table", "Rows", "Query", "Read", "Process", "Total", "Rows/s");
        foreach (var column in table.Columns)
            column.Alignment = Justify.Left;
        table.Columns[1].Alignment = Justify.Right;
        for (var i = 2; i < table.Columns.Count; i++)
            table.Columns[i].Alignment = Justify.Right;
        table.Columns[0].Padding = new Padding(1, 0, 1, 0);

        foreach (var t in result.Tables.OrderBy(t => t.TotalMs))
        {
            table.AddRow(BuildRowMarkup(t));
            // A single-partition table's partition row would repeat the table row.
            if (t.Partitions is { Count: > 1 } partitions)
            {
                foreach (var p in partitions)
                    table.AddRow(BuildPartitionRowMarkup(p));
            }
        }

        if (result.Totals is { } total)
        {
            var totalSeconds = (total.TotalMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            table.AddRow(
                Styling.Bold("Total"),
                Styling.Number(total.Rows),
                Styling.Muted(""),
                Styling.Muted(""),
                DurationMarkup(total.ProcessMs),
                Styling.Muted(totalSeconds),
                Styling.Muted(""));
        }

        AnsiConsole.Write(table);

        if (result.Phases is { Count: > 0 } phases)
        {
            var phaseTable = Styling.NewTable("Phase", "Objects", "Time");
            phaseTable.Columns[1].Alignment = Justify.Right;
            phaseTable.Columns[2].Alignment = Justify.Right;
            phaseTable.Columns[0].Padding = new Padding(1, 0, 1, 0);
            foreach (var phase in phases)
                phaseTable.AddRow(Styling.MarkupEscape(PhaseLabel(phase.Phase)), Styling.Number(phase.Count), DurationMarkup(phase.DurationMs));
            AnsiConsole.WriteLine();
            AnsiConsole.Write(phaseTable);
        }
    }

    /// <summary>Display label for a <see cref="RefreshPhaseResult.Phase"/> name.</summary>
    internal static string PhaseLabel(string phase) => phase switch
    {
        "load" => "Data load",
        "hierarchies" => "Hierarchies",
        "calculatedColumns" => "Calculated columns",
        "relationships" => "Relationships",
        "calculationScript" => "Calculation script",
        "sequencePoint" => "Sequence point",
        "commit" => "Commit",
        _ => phase,
    };

    private static string[] BuildRowMarkup(RefreshTableResult t)
    {
        var rate = t.TotalMs > 0 ? (long)Math.Round(t.Rows * 1000.0 / t.TotalMs) : 0;
        return
        [
            Styling.MarkupEscape(t.Table),
            Styling.Number(t.Rows),
            DurationMarkup(t.QueryMs),
            DurationMarkup(t.ReadMs),
            DurationMarkup(t.ProcessMs),
            DurationMarkup(t.TotalMs),
            rate > 0 ? Styling.Number(rate) : ""
        ];
    }

    private static string[] BuildPartitionRowMarkup(RefreshPartitionResult p)
        =>
        [
            Styling.Muted("  └ " + p.Partition),
            Styling.Muted(p.Rows.ToString("N0", CultureInfo.InvariantCulture)),
            DurationMarkup(p.QueryMs),
            DurationMarkup(p.ReadMs),
            "",
            DurationMarkup(p.TotalMs),
            ""
        ];

    private static string DurationMarkup(long ms)
        => ms > 0 ? Styling.Muted((ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s") : Styling.Muted("0s");

    public static void RenderCsv(RefreshModelResult result)
    {
        if (result.PolicyApplication is { } policy)
        {
            Console.WriteLine("table,effective_date,refreshed,operation");
            foreach (var operation in policy.Operations.DefaultIfEmpty("no changes"))
                Console.WriteLine($"{Csv(policy.Table)},{policy.EffectiveDate:yyyy-MM-dd},false,{Csv(operation)}");
            return;
        }
        if (result.PolicyPreview is { } preview)
        {
            Console.WriteLine("table,effective_date,loads_data,preview");
            Console.WriteLine($"{Csv(preview.Table)},{preview.EffectiveDate:yyyy-MM-dd},false,true");
            return;
        }

        Console.WriteLine("table,rows,query_ms,read_ms,total_ms,rows_per_second");
        foreach (var t in result.Tables)
        {
            var rate = t.TotalMs > 0 ? (long)Math.Round(t.Rows * 1000.0 / t.TotalMs) : 0;
            Console.WriteLine(string.Join(',',
                Csv(t.Table), Csv(t.Rows), Csv(t.QueryMs), Csv(t.ReadMs), Csv(t.TotalMs), Csv(rate)));
        }
        if (result.Totals is { } total)
        {
            var rate = total.TotalMs > 0 ? (long)Math.Round(total.Rows * 1000.0 / total.TotalMs) : 0;
            Console.WriteLine(string.Join(',',
                Csv("Total"), Csv(total.Rows), Csv(total.QueryMs), Csv(total.ReadMs), Csv(total.TotalMs), Csv(rate)));
        }
    }

    private static string Csv(string value)
    {
        // Minimal CSV escaping: wrap in quotes if it contains a comma, quote, or newline.
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Csv<T>(T value) where T : struct, IFormattable
        => value.ToString(null, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// Pretty-prints a compact TMSL JSON script with 2-space indentation.
    /// </summary>
    public static void WriteTmsl(string script)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(script);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                doc.RootElement.WriteTo(writer);
            }

            Console.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        catch
        {
            // If parsing fails, just print the raw script.
            Console.WriteLine(script);
        }
    }
}
