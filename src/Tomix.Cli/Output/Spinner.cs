using Spectre.Console;
using Tomix.App.Mutations;

namespace Tomix.Cli.Output;

internal static class CliSpinner
{
    /// <summary>How long a spinner that waits on a remote host runs before it says so.</summary>
    internal static readonly TimeSpan WaitingNoticeDelay = TimeSpan.FromSeconds(10);

    /// <param name="waitingOn">
    /// The remote host the action waits on, if any. After <paramref name="waitingAfter"/>
    /// (default <see cref="WaitingNoticeDelay"/>) the label names it and how to stop, so a stalled
    /// connection is never a silent spinner. See <see cref="WaitTarget"/>.
    /// </param>
    public static Task RunAsync(
        string label, Func<Task> action, bool suppress = false, string? waitingOn = null, TimeSpan? waitingAfter = null)
        => RunAsync(
            label,
            async () =>
            {
                await action();
                return true;
            },
            suppress,
            waitingOn,
            waitingAfter);

    /// <inheritdoc cref="RunAsync(string, Func{Task}, bool, string?, TimeSpan?)"/>
    public static async Task<T> RunAsync<T>(
        string label, Func<Task<T>> action, bool suppress = false, string? waitingOn = null, TimeSpan? waitingAfter = null)
    {
        if (suppress || ShouldSuppress())
            return await action();

        var delay = waitingAfter ?? WaitingNoticeDelay;
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            using var _ = ReportToStdErr(label);
            return waitingOn is null
                ? await action()
                : await WithWaitingNotice(action(), delay, () => StdErr.MarkupLine(Styling.Muted(WaitingMessage(waitingOn))));
        }

        return await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(new Style(Palette.Info))
            .StartAsync(label, async ctx =>
            {
                using var _ = ReportToStatus(ctx);
                return waitingOn is null
                    ? await action()
                    : await WithWaitingNotice(action(), delay, () => ctx.Status(Styling.MarkupEscape(WaitingMessage(waitingOn))));
            });
    }

    /// <summary>Awaits <paramref name="work"/>, calling <paramref name="onSlow"/> once if it outlasts <paramref name="delay"/>.</summary>
    internal static async Task<T> WithWaitingNotice<T>(Task<T> work, TimeSpan delay, Action onSlow)
    {
        using var timer = new CancellationTokenSource();
        var elapsed = Task.Delay(delay, timer.Token);
        if (await Task.WhenAny(work, elapsed) == elapsed && !work.IsCompleted)
            onSlow();
        await timer.CancelAsync();
        return await work;
    }

    internal static string WaitingMessage(string host) => $"Still waiting for {host}... (Ctrl+C to cancel)";

    /// <summary>
    /// The host a command waits on when its server is remote (<c>powerbi://</c>, <c>asazure://</c>,
    /// <c>http(s)://</c>); <c>null</c> for local models and instances, which never wait on a network.
    /// </summary>
    internal static string? WaitTarget(string? server)
        => Uri.TryCreate(server?.Trim(), UriKind.Absolute, out var uri)
           && uri.Scheme is "powerbi" or "asazure" or "http" or "https"
           && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    /// <summary>
    /// Routes handler-phase progress (e.g. "Syncing to &lt;workspace&gt;...") to the live status
    /// label, so a long network phase is visible instead of hiding behind "Saving...".
    /// </summary>
    private static IDisposable ReportToStatus(StatusContext ctx)
        => MutationProgress.Use(message => ctx.Status(Styling.MarkupEscape(message)));

    /// <summary>
    /// A terminal that cannot animate (stdin piped, as in <c>$secret | tx auth login --password -</c>):
    /// Spectre's fallback would print the label as plain text on stdout, so print it, and each
    /// progress phase, once as dim commentary on stderr instead.
    /// </summary>
    private static IDisposable ReportToStdErr(string label)
    {
        StdErr.MarkupLine(Styling.Muted(label));
        return MutationProgress.Use(message => StdErr.MarkupLine(Styling.Muted(message)));
    }

    public static bool ShouldSuppress() => Console.IsOutputRedirected;
}
