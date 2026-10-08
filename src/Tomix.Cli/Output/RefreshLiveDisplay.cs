using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Tomix.App.Refresh;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Output;

/// <summary>
/// Live refresh panel: a header with the elapsed time and overall progress, then one line per
/// in-progress table (its current step, partition, rows, and how long it has been running),
/// oldest first and capped, then the model-level step once the tables are done.
/// <para>
/// Trace events arrive on the trace thread and only update shared state under a lock; the panel
/// is redrawn by a loop on the <see cref="AnsiConsole.Live"/> context, so Spectre is never
/// touched from the trace thread. The panel clears when the refresh ends and the summary table
/// renders in its place.
/// </para>
/// </summary>
internal sealed class RefreshLiveDisplay : IDisposable
{
    /// <summary>How many in-progress tables the panel lists; the rest are counted.</summary>
    internal const int MaxListedTables = 6;

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(125);

    private readonly Dictionary<string, TableView> _tables = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // Latest model-level step (relationships, calculation script, commit); shown once no table is active.
    private string? _modelPhase;

    /// <summary>A table as the panel shows it. <see cref="Started"/> is when its current run of work began.</summary>
    internal sealed record TableView(string Name, string? Partition, string? Phase, long Rows, bool Completed, TimeSpan Started);

    public RefreshLiveDisplay()
    {
        // SynchronousProgress calls OnReport directly on the trace thread, so state is current
        // the moment an event arrives; the render loop picks it up on its next frame.
        Progress = new SynchronousProgress(OnReport);
    }

    public IProgress<RefreshProgress> Progress { get; }

    private void OnReport(RefreshProgress p)
    {
        lock (_lock)
        {
            if (p.Table is null)
            {
                _modelPhase = p.Phase;
                return;
            }
            if (p.Table.Length == 0)
                return;

            var now = _clock.Elapsed;
            _tables.TryGetValue(p.Table, out var existing);
            // A finished table that starts post-load work (hierarchies) begins a new run.
            var started = existing is null || (existing.Completed && !p.Completed) ? now : existing.Started;
            _tables[p.Table] = new TableView(
                p.Table,
                p.Partition,
                p.Phase ?? existing?.Phase,
                p.RowsRead ?? existing?.Rows ?? 0,
                p.Completed,
                started);
        }
    }

    private IRenderable Render(string label)
    {
        List<TableView> tables;
        string? modelPhase;
        lock (_lock)
        {
            tables = _tables.Values.ToList();
            modelPhase = _modelPhase;
        }
        var lines = BuildLines(label, _clock.Elapsed, tables, modelPhase);
        return new Rows(lines.Select(l => new Markup(l)));
    }

    /// <summary>
    /// Composes the panel as markup lines. Pure so ordering, capping, formatting, and escaping are
    /// unit-testable: every name is escaped, since a raw <c>Sales [EUR]</c> is invalid markup.
    /// </summary>
    internal static IReadOnlyList<string> BuildLines(
        string label, TimeSpan elapsed, IReadOnlyList<TableView> tables, string? modelPhase, int maxListed = MaxListedTables)
    {
        var frames = Spectre.Console.Spinner.Known.Dots.Frames;
        var frame = frames[(int)(elapsed.TotalMilliseconds / Spectre.Console.Spinner.Known.Dots.Interval.TotalMilliseconds) % frames.Count];
        var lines = new List<string>
        {
            $"[{Palette.Info.ToMarkup()}]{Styling.MarkupEscape(frame)}[/] {Styling.MarkupEscape(label.TrimEnd('.'))} {Styling.Muted("· " + Clock(elapsed))}",
        };

        // Oldest first: the order is stable, and long-running tables stay in view.
        var active = tables.Where(t => !t.Completed).OrderBy(t => t.Started).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        var done = tables.Count(t => t.Completed);
        var rows = tables.Sum(t => t.Rows);

        if (tables.Count == 0)
        {
            lines.Add("  " + Styling.Muted(modelPhase is null ? "Waiting for the server..." : Sentence(modelPhase)));
            return lines;
        }

        var summary = $"{Styling.Number(done)} {(done == 1 ? "table" : "tables")} done · {Styling.Number(active.Count)} in progress";
        if (rows > 0)
            summary += $" · {Styling.Number(rows)} rows";
        lines.Add("  " + Styling.Muted(summary));

        if (active.Count == 0)
        {
            if (modelPhase is not null)
                lines.Add("  " + Sentence(modelPhase));
            return lines;
        }

        var listed = active.Take(maxListed).ToList();
        var names = listed.Select(t => t.Partition is null ? t.Name : $"{t.Name} › {t.Partition}").ToList();
        var nameWidth = Math.Min(names.Max(n => n.Length), 48);
        for (var i = 0; i < listed.Count; i++)
        {
            var t = listed[i];
            var name = Fit(names[i], nameWidth);
            var count = t.Rows > 0 ? $"{Styling.Number(t.Rows)} rows" : "";
            lines.Add(
                $"  {Styling.Muted(Verb(t.Phase).PadRight(13))}" +
                $"{Styling.MarkupEscape(name.PadRight(nameWidth))}  " +
                $"{Styling.MarkupEscape(count.PadLeft(16))}  " +
                Styling.Muted(Clock(elapsed - t.Started)));
        }
        if (active.Count > listed.Count)
            lines.Add("  " + Styling.Muted($"+ {Styling.Number(active.Count - listed.Count)} more in progress"));
        return lines;
    }

    private static string Verb(string? phase) => phase switch
    {
        "query" => "Querying",
        "read" => "Reading",
        "compress" => "Compressing",
        "hierarchies" => "Hierarchies",
        "calculated columns" => "Calc columns",
        _ => "Processing",
    };

    private static string Sentence(string phase) => char.ToUpperInvariant(phase[0]) + phase[1..] + "...";

    private static string Clock(TimeSpan t)
        => t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string Fit(string text, int width)
        => text.Length <= width ? text : text[..(width - 1)] + "…";

    public async Task<TomixResult<RefreshModelResult>> RunAsync(string label, Func<Task<TomixResult<RefreshModelResult>>> action)
    {
        TomixResult<RefreshModelResult>? captured = null;
        await AnsiConsole.Live(Render(label))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                var work = action();
                while (!work.IsCompleted)
                {
                    ctx.UpdateTarget(Render(label));
                    await Task.WhenAny(work, Task.Delay(RenderInterval)).ConfigureAwait(false);
                }
                captured = await work.ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        return captured!;
    }

    public void Dispose() { }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> adapter: calls the handler immediately on the
/// reporting thread instead of posting to the thread pool like <see cref="Progress{T}"/>.
/// Used by <see cref="RefreshLiveDisplay"/> so trace events update its state in order.
/// </summary>
internal sealed class SynchronousProgress : IProgress<RefreshProgress>
{
    private readonly Action<RefreshProgress> _handler;
    public SynchronousProgress(Action<RefreshProgress> handler) => _handler = handler;
    public void Report(RefreshProgress value) => _handler(value);
}
