using Spectre.Console;
using Tomix.App.Refresh;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Output;

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
