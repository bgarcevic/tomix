using System.CommandLine;
using Spectre.Console;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

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
            promptForbidden: !InteractionGate.CanPrompt(parseResult, outputFormat),
            GlobalOptions.ErrorFormatValue(parseResult, outputFormat));

    private static bool Confirm(
        string action,
        string subject,
        bool yes,
        bool promptForbidden,
        string? errorFormat)
    {
        if (yes)
            return true;

        if (promptForbidden)
        {
            // Through ErrorOutput, not raw markup: this is the failure a scripted caller actually
            // hits (json/csv output and --non-interactive both forbid the prompt), so it needs a
            // code to branch on rather than prose to grep.
            // The subject belongs in the message; the hint is the remediation, per
            // docs/error-codes.md, so it must say what to do rather than restate the action.
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_CONFIRMATION_REQUIRED",
                    DiagnosticSeverity.Error,
                    $"{action} {subject} needs confirmation.",
                    // Not "re-run without --non-interactive": InteractionGate also refuses to
                    // prompt under --quiet, json/csv output, and redirected stdin/stderr, so
                    // naming one cause misdirects the caller who hit any of the others.
                    "Pass --yes to confirm.")],
                errorFormat);
            return false;
        }

        var errConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });
        return errConsole.Confirm($"  {Styling.MarkupEscape(action)} {Styling.MarkupEscape(subject)}?", defaultValue: false);
    }
}
