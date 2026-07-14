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

        await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(Palette.Sage.ToMarkup()))
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

        return await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(Palette.Sage.ToMarkup()))
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

    public static bool ShouldSuppress() => Console.IsOutputRedirected;
}
