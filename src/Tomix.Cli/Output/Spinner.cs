using Spectre.Console;
using Tomix.App.Mutations;

namespace Tomix.Cli.Output;

internal static class CliSpinner
{
    public static async Task RunAsync(string label, Func<Task> action, bool suppress = false)
    {
        if (suppress || ShouldSuppress())
        {
            await action();
            return;
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            using var _ = ReportToStdErr(label);
            await action();
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(new Style(Palette.Info))
            .StartAsync(label, async ctx =>
            {
                using var _ = ReportToStatus(ctx);
                await action();
            });
    }

    public static async Task<T> RunAsync<T>(string label, Func<Task<T>> action, bool suppress = false)
    {
        if (suppress || ShouldSuppress())
            return await action();

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            using var _ = ReportToStdErr(label);
            return await action();
        }

        return await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(new Style(Palette.Info))
            .StartAsync(label, async ctx =>
            {
                using var _ = ReportToStatus(ctx);
                return await action();
            });
    }

    /// <summary>
    /// Routes handler-phase progress (e.g. "Syncing to &lt;workspace&gt;...") to the live status
    /// label, so a long network phase is visible instead of hiding behind "Saving...".
    /// </summary>
    private static IDisposable ReportToStatus(StatusContext ctx)
        => MutationProgress.Use(message => ctx.Status(Styling.MarkupEscape(message)));

    /// <summary>
    /// A terminal that cannot animate (stdin piped, as in <c>$secret | tx auth login -p -</c>):
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
