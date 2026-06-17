using Spectre.Console;

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
            .StartAsync(label, _ => action());
    }

    public static async Task<T> RunAsync<T>(string label, Func<Task<T>> action, bool suppress = false)
    {
        if (suppress || ShouldSuppress())
            return await action();

        return await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(Palette.Sage.ToMarkup()))
            .StartAsync(label, _ => action());
    }

    public static bool ShouldSuppress() => Console.IsOutputRedirected;
}
