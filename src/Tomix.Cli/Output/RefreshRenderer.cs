using System.Globalization;
using Spectre.Console;
using Tomix.App.Refresh;
using RefreshTableResult = Tomix.Core.Models.RefreshTableResult;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering for the <c>refresh</c> command: header + per-table statistics table (text),
/// CSV rows, and the <c>--dry-run</c> TMSL script pretty-print.
/// </summary>
internal static class RefreshRenderer
{
    public static void Render(RefreshModelResult result)
    {
        var database = string.IsNullOrWhiteSpace(result.Database) ? "<model>" : result.Database;
        var server = string.IsNullOrWhiteSpace(result.Server) ? "<endpoint>" : result.Server;
        var seconds = Styling.DurationSeconds(result.DurationMs / 1000.0);

        var header =
            $"[{Palette.Moss.ToMarkup()}]Refreshed[/] " +
            $"[{Palette.Terra.ToMarkup()}]{Styling.MarkupEscape(database)}[/] " +
            $"[{Palette.Moss.ToMarkup()}]on[/] " +
            $"[{Palette.Harbor.ToMarkup()}]{Styling.MarkupEscape(server)}[/] " +
            $"[{Palette.Moss.ToMarkup()}]({seconds})[/]";
        AnsiConsole.MarkupLine(header);
        AnsiConsole.WriteLine();

        if (result.Tables.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("No per-table statistics available. Use without --no-progress to capture XMLA trace events."));
            return;
        }

        var table = Styling.NewTable("Table", "Rows", "Query", "Read", "Total", "Rows/s");
        foreach (var column in table.Columns)
            column.Alignment = Justify.Left;
        table.Columns[1].Alignment = Justify.Right;
        for (var i = 2; i < table.Columns.Count; i++)
            table.Columns[i].Alignment = Justify.Right;
        table.Columns[0].Padding = new Padding(1, 0, 1, 0);

        foreach (var t in result.Tables.OrderBy(t => t.TotalMs))
            table.AddRow(BuildRowMarkup(t));

        if (result.Totals is { } total)
        {
            // Build bold-styled values directly. Styling.Bold(Styling.Muted(...)) would
            // double-escape the brackets; the Slate palette constant is the muted color.
            var slate = Palette.Slate.ToMarkup();
            var totalSeconds = (total.TotalMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            table.AddRow(
                Styling.Bold("Total"),
                Styling.Number(total.Rows),
                Styling.Muted(""),
                Styling.Muted(""),
                $"[{slate}]{totalSeconds}[/]",
                Styling.Muted(""));
        }

        AnsiConsole.Write(table);
    }

    private static string[] BuildRowMarkup(RefreshTableResult t)
    {
        var rate = t.TotalMs > 0 ? (long)Math.Round(t.Rows * 1000.0 / t.TotalMs) : 0;
        return
        [
            Styling.MarkupEscape(t.Table),
            Styling.Number(t.Rows),
            DurationMarkup(t.QueryMs),
            DurationMarkup(t.ReadMs),
            DurationMarkup(t.TotalMs),
            rate > 0 ? Styling.Number(rate) : ""
        ];
    }

    private static string DurationMarkup(long ms)
        => ms > 0 ? Styling.Muted((ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s") : Styling.Muted("0s");

    public static void RenderCsv(RefreshModelResult result)
    {
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
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc.RootElement, options));
        }
        catch
        {
            // If parsing fails, just print the raw script.
            Console.WriteLine(script);
        }
    }
}
