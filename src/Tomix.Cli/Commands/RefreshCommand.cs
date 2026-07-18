using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Spectre.Console;
using Tomix.App.Refresh;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;
using RefreshTableResult = Tomix.Core.Models.RefreshTableResult;

namespace Tomix.Cli.Commands;

internal sealed class RefreshCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public RefreshCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var typeOption = new Option<string?>("--type")
        {
            Description = "Refresh type: full, dataonly, automatic, calculate, clearvalues, defragment, add (default: automatic)"
        };

        var tableOption = new Option<string[]>("--table")
        {
            Description = "Specific table(s) to refresh. If omitted, refreshes the entire model. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var partitionOption = new Option<string[]>("--partition")
        {
            Description = "Specific partition(s) to refresh as TableName.PartitionName. Requires --table to be omitted. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var applyRefreshPolicyOption = new Option<bool?>("--apply-refresh-policy")
        {
            Description = "Apply incremental refresh policy (default: true). Set to false to skip policy-based partitioning.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var skipRefreshPolicyOption = new Option<bool>("--skip-refresh-policy")
        {
            Description = "Shorthand for --apply-refresh-policy false."
        };

        var effectiveDateOption = new Option<DateOnly?>("--effective-date")
        {
            Description = "Override the current date for incremental refresh policy evaluation (format: yyyy-MM-dd)."
        };

        var maxParallelismOption = new Option<int?>("--max-parallelism")
        {
            Description = "Maximum parallel refresh operations."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Output the TMSL script without executing it."
        };

        var noProgressOption = new Option<bool>("--no-progress")
        {
            Description = "Disable live progress tracking (for CI/piping)."
        };

        var traceOption = new Option<string?>("--trace")
        {
            Description = "Dump raw XMLA trace events. No value = stderr, path = write to log file.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("refresh", "Trigger a data refresh on a deployed model (--type full|auto|calculate|...)")
        {
            typeOption,
            tableOption,
            partitionOption,
            applyRefreshPolicyOption,
            skipRefreshPolicyOption,
            effectiveDateOption,
            maxParallelismOption,
            dryRunOption,
            noProgressOption,
            traceOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "refresh", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var type = parseResult.GetValue(typeOption) ?? "automatic";
            var tables = parseResult.GetValue(tableOption);
            var partitions = ParsePartitions(parseResult.GetValue(partitionOption), out var badPartition);
            if (partitions is null)
            {
                ErrorOutput.Write(
                    new[]
                    {
                        new TomixDiagnostic(
                            "TOMIX_REFRESH_BAD_PARTITION",
                            DiagnosticSeverity.Error,
                            $"Invalid --partition value '{badPartition}'. Expected TableName.PartitionName.",
                            Hint: "Example: --partition Sales.Internet")
                    },
                    parseResult.GetValue(GlobalOptions.ErrorFormat));
                return 2;
            }

            var applyPolicy = ResolveApplyPolicy(parseResult.GetValue(applyRefreshPolicyOption), parseResult.GetValue(skipRefreshPolicyOption));
            var effectiveDate = parseResult.GetValue(effectiveDateOption);
            var maxParallelism = parseResult.GetValue(maxParallelismOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var noProgress = parseResult.GetValue(noProgressOption);
            // ArgumentArity.ZeroOrOne surfaces both "absent" and "bare --trace" as null from GetValue.
            // Gate on GetResult so absent stays off, while bare --trace resolves to stderr ("-").
            var tracePath = parseResult.GetResult(traceOption) is null
                ? null
                : ResolveTracePath(parseResult.GetValue(traceOption));

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult),
                    out var source,
                    out var recentExit))
                return recentExit;

            var request = new RefreshModelRequest(
                Model: source.Model,
                Server: source.Server,
                Database: source.Database,
                Auth: GlobalOptions.AuthValue(parseResult),
                RefreshType: type,
                Tables: tables is { Length: > 0 } ? tables : null,
                Partitions: partitions is { Count: > 0 } ? partitions : null,
                ApplyRefreshPolicy: applyPolicy,
                EffectiveDate: effectiveDate,
                MaxParallelism: maxParallelism,
                DryRun: dryRun,
                NoProgress: noProgress,
                TracePath: tracePath);

            // When --recent picked the target, resolve against that entry (not the active session)
            // so the refresh target and its workspace mirror come from the recent connection.
            var recentSession = RecentConnections.SessionSource(source);
            RefreshModelHandler CreateHandler() => recentSession is null
                ? new RefreshModelHandler(_providers)
                : new RefreshModelHandler(_providers, recentSession);

            // Progress + trace sinks: live spinner display via AnsiConsole.Status, plus optional --trace file/stderr.
            var suppressProgress = noProgress || quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format) || CliSpinner.ShouldSuppress();
            using var traceWriter = OpenTraceWriter(tracePath, quiet);
            try
            {
                // --dry-run never executes; no live display, just emit the script.
                if (dryRun)
                {
                    var dryResult = await CreateHandler()
                        .HandleAsync(request, progress: null, traceWriter, cancellationToken)
                        .ConfigureAwait(false);

                    if (dryResult.Success && dryResult.Data?.Script is not null)
                    {
                        if (OutputFormats.IsJson(format))
                            JsonOutput.Write(dryResult.Data);
                        else
                            PrettyPrintTmsl(dryResult.Data.Script);
                    }
                    else
                    {
                        ErrorOutput.Write(dryResult.Diagnostics, parseResult.GetValue(GlobalOptions.ErrorFormat));
                    }
                    return dryResult.ExitCode == 0 && dryResult.Success ? 0 : dryResult.ExitCode;
                }

                TomixResult<RefreshModelResult> result;
                if (suppressProgress)
                {
                    result = await CreateHandler()
                        .HandleAsync(request, progress: null, traceWriter, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var display = new RefreshLiveDisplay();
                    result = await display.RunAsync(
                        BuildSpinnerLabel(request),
                        () => CreateHandler().HandleAsync(request, display.Progress, traceWriter, cancellationToken))
                        .ConfigureAwait(false);
                }

                return CommandOutput.Render(
                    result,
                    format,
                    data => Render(data, request),
                    data => data,
                    data => RenderCsv(data),
                    errorFormat: parseResult.GetValue(GlobalOptions.ErrorFormat));
            }
            finally
            {
                if (traceWriter is not null)
                    await traceWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });

        return command;
    }

    internal static IReadOnlyList<TablePartition>? ParsePartitions(string[]? raw, out string? badValue)
    {
        badValue = null;
        if (raw is null || raw.Length == 0)
            return Array.Empty<TablePartition>();

        var list = new List<TablePartition>(raw.Length);
        foreach (var value in raw)
        {
            var dot = value.IndexOf('.');
            if (dot <= 0 || dot >= value.Length - 1)
            {
                badValue = value;
                return null;
            }
            list.Add(new TablePartition(value[..dot], value[(dot + 1)..]));
        }
        return list;
    }

    internal static bool ResolveApplyPolicy(bool? explicitValue, bool skipFlag)
    {
        if (skipFlag) return false;
        if (explicitValue.HasValue) return explicitValue.Value;
        return true;
    }

    /// <summary>
    /// Normalizes the <c>--trace</c> option value. Bare <c>--trace</c> (no value) and
    /// <c>--trace -</c> both map to stderr (<c>"-"</c>); any other non-empty value is treated
    /// as a file path. Returns null only when <c>--trace</c> is absent.
    /// </summary>
    internal static string? ResolveTracePath(string? traceValue)
        => string.IsNullOrEmpty(traceValue) ? "-" : traceValue;

    /// <summary>
    /// Opens a trace writer for <c>--trace</c>: null (off), "-" or empty (stderr), or a path (file).
    /// Returns null when <paramref name="tracePath"/> is null. Tracing is independent of progress.
    /// </summary>
    internal static TextWriter? OpenTraceWriter(string? tracePath, bool quiet)
    {
        if (string.IsNullOrEmpty(tracePath))
            return null;

        if (tracePath == "-")
            return quiet ? TextWriter.Null : NonDisposingTextWriter.Wrap(Console.Error);

        try
        {
            var full = Path.GetFullPath(tracePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new StreamWriter(full, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open --trace file '{tracePath}': {ex.Message}");
            return NonDisposingTextWriter.Wrap(Console.Error);
        }
    }

    private static string BuildSpinnerLabel(RefreshModelRequest request)
    {
        var type = string.IsNullOrWhiteSpace(request.RefreshType) ? "automatic" : request.RefreshType;
        if (request.Partitions is { Count: > 0 })
            return $"Refreshing {request.Partitions.Count} partition(s) ({type})...";
        if (request.Tables is { Count: > 0 })
            return $"Refreshing {request.Tables.Count} table(s) ({type})...";
        return $"Refreshing model ({type})...";
    }

    /// <summary>
    /// Pretty-prints a compact TMSL JSON script with 2-space indentation.
    /// </summary>
    private static void PrettyPrintTmsl(string script)
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

    private static void Render(RefreshModelResult result, RefreshModelRequest request)
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

    private static void RenderCsv(RefreshModelResult result)
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
}

/// <summary>
/// Live refresh display using <c>AnsiConsole.Status()</c>: a spinner whose status label updates
/// in real time as trace events fire. <c>Status()</c> runs its own render timer, so we just set
/// <c>ctx.Status</c> from the trace thread — no <c>Refresh()</c> calls needed and no cross-thread
/// rendering issues like with <c>AnsiConsole.Live()</c>. The final summary table renders after
/// the refresh completes.
/// </summary>
internal sealed class RefreshLiveDisplay : IDisposable
{
    private readonly Dictionary<string, (long Rows, string Phase, bool Completed)> _rows = new(StringComparer.Ordinal);
    private readonly object _rowsLock = new();
    private StatusContext? _ctx;

    public RefreshLiveDisplay()
    {
        // SynchronousProgress calls OnReport directly on the trace thread during server.Execute,
        // so the status label updates immediately when ProgressReportEnd events fire.
        Progress = new SynchronousProgress(OnReport);
    }

    public IProgress<RefreshProgress> Progress { get; }

    private void OnReport(RefreshProgress p)
    {
        if (!string.IsNullOrEmpty(p.Table))
        {
            lock (_rowsLock)
            {
                if (_rows.TryGetValue(p.Table, out var existing))
                {
                    existing.Rows = p.RowsRead ?? existing.Rows;
                    existing.Phase = p.Phase ?? existing.Phase;
                    existing.Completed = p.Completed;
                    _rows[p.Table] = existing;
                }
                else
                {
                    _rows[p.Table] = (p.RowsRead ?? 0, p.Phase ?? "", p.Completed);
                }
            }
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_ctx is null) return;

        List<(string Name, long Rows, string Phase, bool Completed)> snapshot;
        lock (_rowsLock)
        {
            snapshot = _rows
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value.Rows, kv.Value.Phase, kv.Value.Completed))
                .ToList();
        }

        var active = snapshot.Where(s => !s.Completed).ToList();
        if (active.Count == 0) return;

        var parts = active.Select(s =>
        {
            var detail = s.Rows > 0 ? $" {s.Rows:N0} rows" : "";
            var phaseStr = s.Phase ?? "processing";
            return $"{s.Name}{detail} {phaseStr}".Trim();
        });

        _ctx.Status = string.Join("  |  ", parts);
    }

    public async Task<TomixResult<RefreshModelResult>> RunAsync(string label, Func<Task<TomixResult<RefreshModelResult>>> action)
    {
        TomixResult<RefreshModelResult>? captured = null;
        await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(Palette.Sage.ToMarkup()))
            .StartAsync(label, async ctx =>
            {
                _ctx = ctx;
                captured = await action().ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        return captured!;
    }

    public void Dispose() { }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> adapter: calls the handler immediately on the
/// reporting thread instead of posting to the thread pool like <see cref="Progress{T}"/>.
/// Used by <see cref="RefreshLiveDisplay"/> so trace events during server.Execute update the
/// live table in real time.
/// </summary>
internal sealed class SynchronousProgress : IProgress<RefreshProgress>
{
    private readonly Action<RefreshProgress> _handler;
    public SynchronousProgress(Action<RefreshProgress> handler) => _handler = handler;
    public void Report(RefreshProgress value) => _handler(value);
}

/// <summary>
/// Delegates all writes to a shared <see cref="TextWriter"/> (for example
/// <see cref="Console.Error"/>) but never disposes it. The CLI owns only the
/// <see cref="StreamWriter"/> instances it opens for <c>--trace &lt;file&gt;</c>;
/// <see cref="Console.Error"/> is process-shared and must survive the command's
/// <c>using</c> scope, otherwise later stderr writes throw
/// <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class NonDisposingTextWriter : TextWriter
{
    private readonly TextWriter _inner;

    private NonDisposingTextWriter(TextWriter inner) => _inner = inner;

    public static NonDisposingTextWriter Wrap(TextWriter inner) => new(inner);

    /// <summary>The wrapped shared writer (exposed for assertions).</summary>
    public TextWriter Inner => _inner;

    public override System.Text.Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);
    public override void WriteLine() => _inner.WriteLine();
    public override void WriteLine(string? value) => _inner.WriteLine(value);
    public override void WriteLine(char value) => _inner.WriteLine(value);
    public override Task WriteAsync(char value) => _inner.WriteAsync(value);
    public override Task WriteAsync(string? value) => _inner.WriteAsync(value);
    public override Task WriteLineAsync(string? value) => _inner.WriteLineAsync(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync() => _inner.FlushAsync();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        // Intentionally do not dispose the shared inner writer (e.g. Console.Error).
        base.Dispose(disposing);
    }
}
