using System.CommandLine;
using Spectre.Console;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal enum PreviewDecision { Apply, Declined, PreviewOnly }

/// <summary>
/// Preview-first gate for commands that act on something outside the model file (<c>deploy</c>,
/// partition-risky <c>refresh</c>). Without <c>--yes</c> the command renders its preview,
/// then asks here: an interactive terminal is prompted, and every non-promptable context stops
/// after the preview with <see cref="PreviewExitCode"/>, so a script that forgot <c>--yes</c>
/// gets a safe preview it can tell apart from a real run.
/// </summary>
internal static class PreviewGate
{
    /// <summary>Exit code for "previewed, nothing applied" in a context that cannot prompt.</summary>
    public const int PreviewExitCode = 3;

    /// <summary>True when the command should render a preview first, i.e. <c>--yes</c> was not passed.</summary>
    public static bool PreviewFirst(ParseResult parseResult) => !parseResult.GetValue(GlobalOptions.Yes);

    /// <summary>
    /// Called after the preview was rendered. Prompts when <see cref="InteractionGate"/> allows it;
    /// otherwise notes on stderr (text output only; JSON callers read <c>status</c>) that nothing
    /// was applied.
    /// </summary>
    public static PreviewDecision Decide(string action, string subject, ParseResult parseResult, string outputFormat)
    {
        if (!InteractionGate.CanPrompt(parseResult, outputFormat))
        {
            if (OutputFormats.IsTextLike(outputFormat))
                StdErr.MarkupLine(Styling.Guidance("Preview only: nothing was applied. Pass --yes to apply."));
            return PreviewDecision.PreviewOnly;
        }

        var errConsole = StdErr.Console();
        errConsole.WriteLine();
        return errConsole.Confirm($"  {Styling.MarkupEscape(action)} {Styling.MarkupEscape(subject)}?", defaultValue: false)
            ? PreviewDecision.Apply
            : PreviewDecision.Declined;
    }

    /// <summary>The exit code for a gate that did not apply: 3 for a preview-only run, 1 for a declined prompt.</summary>
    public static int ExitCode(PreviewDecision decision) => decision == PreviewDecision.PreviewOnly ? PreviewExitCode : 1;
}
