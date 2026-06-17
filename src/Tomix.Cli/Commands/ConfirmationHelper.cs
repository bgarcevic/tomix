using Tomix.Cli.Output;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal static class ConfirmationHelper
{
    public static bool ConfirmOrAbort(
        string action,
        string subject,
        bool yes,
        bool nonInteractive)
    {
        if (yes)
            return true;

        if (nonInteractive || Console.IsInputRedirected)
        {
            var err = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error)
            });
            err.MarkupLine(Styling.Error($"Pass --yes to confirm {action}."));
            return false;
        }

        var errConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });
        return errConsole.Confirm($"  {Styling.MarkupEscape(action)} {Styling.MarkupEscape(subject)}?", defaultValue: false);
    }
}
