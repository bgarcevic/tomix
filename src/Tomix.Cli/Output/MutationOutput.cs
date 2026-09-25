using Spectre.Console;
using Tomix.App.Mutations;

namespace Tomix.Cli.Output;

/// <summary>
/// The shared text rendering of a mutation's persistence outcome (the lines under the
/// command-specific "Added:"/"Moved:"/"Set:" line), so every mutation command states what was
/// kept the same way.
/// </summary>
internal static class MutationOutput
{
    internal const string NotSavedHint = "Not saved yet. Pass --save to persist, or --stage to stage the change.";

    internal const string LiveModelNotice =
        "Saved to the running Power BI Desktop model. Save the report in Power BI Desktop to keep the change.";

    /// <summary>Renders staged / dry-run / not-saved / saved, then the workspace sync line.</summary>
    public static void RenderPersistence(MutationOutcome outcome, string indent = "")
    {
        switch (outcome.Status)
        {
            case MutationStatus.Staged:
                AnsiConsole.MarkupLine(indent + Styling.Guidance("Staged. Run 'tx stage commit' to promote."));
                break;
            case MutationStatus.DryRun:
                AnsiConsole.MarkupLine(indent + Styling.Guidance("Dry run: nothing was saved."));
                break;
            case MutationStatus.Preview:
                AnsiConsole.MarkupLine(indent + Styling.Warning(NotSavedHint));
                break;
            case MutationStatus.Saved:
                RenderSaved(outcome, indent);
                break;
        }

        RenderSync(outcome, indent);
    }

    /// <summary>
    /// "Saved: &lt;where&gt;", plus a notice on stderr when the save landed in Power BI Desktop's
    /// in-memory model, which does not survive closing Desktop without saving the report.
    /// </summary>
    public static void RenderSaved(MutationOutcome outcome, string indent = "")
    {
        AnsiConsole.MarkupLine(indent + Styling.Success($"Saved: {outcome.SavedTo}"));
        RenderLiveModelNotice(outcome);
    }

    /// <summary>Commentary, so it goes to stderr and never pollutes piped results.</summary>
    public static void RenderLiveModelNotice(MutationOutcome outcome)
    {
        if (outcome.Persistence == PersistenceKind.LiveModel)
            StdErr().MarkupLine(Styling.Guidance(LiveModelNotice));
    }

    public static void RenderSync(MutationOutcome outcome, string indent = "")
    {
        if (outcome.Sync is not { } sync)
            return;

        if (sync.Status == SyncStatus.Succeeded)
            AnsiConsole.MarkupLine(indent + Styling.Success($"Synced: {sync.Target}"));
        else if (sync.Warning is not null)
            AnsiConsole.MarkupLine(indent + Styling.Warning(sync.Warning));
    }

    // Created per call: tests swap Console.Error, so a cached console would miss the swap.
    private static IAnsiConsole StdErr()
        => AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
}
