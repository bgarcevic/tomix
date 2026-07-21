using System.CommandLine;
using Spectre.Console;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal static class ConfirmationHelper
{
    /// <summary>
    /// Confirms a destructive action: <c>--yes</c> bypasses, and the prompt is shown only when
    /// <see cref="InteractionGate"/> allows it — every non-promptable context (non-interactive,
    /// quiet, json/csv output, redirected stdin/stderr) fails fast with the flag that would
    /// have answered it.
    /// </summary>
    public static bool ConfirmOrAbort(
        string action,
        string subject,
        ParseResult parseResult,
        string outputFormat)
        => Confirm(
            action,
            subject,
            parseResult.GetValue(GlobalOptions.Yes),
            promptForbidden: !InteractionGate.CanPrompt(parseResult, outputFormat));

    public static bool ConfirmOrAbort(
        string action,
        string subject,
        bool yes,
        bool nonInteractive)
        => Confirm(
            action,
            subject,
            yes,
            promptForbidden: nonInteractive || Console.IsInputRedirected);

    private static bool Confirm(
        string action,
        string subject,
        bool yes,
        bool promptForbidden)
    {
        if (yes)
            return true;

        if (promptForbidden)
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
