using System.CommandLine;
using Spectre.Console;
using Tomix.App.Session;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Interactive;

/// <summary>
/// The commands that exist only inside <c>tx interactive</c>: undo and redo, explicit
/// transactions, status and history. <c>exit</c> belongs to the loop, <c>connect</c> switches models, and
/// everything else is the ordinary command tree.
/// </summary>
internal static class SessionCommands
{
    /// <summary>The names <see cref="Build"/> registers, so a session with no model can explain them.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(["undo", "redo", "begin", "commit", "rollback", "status", "history", "reload"], StringComparer.Ordinal);

    public static IEnumerable<Command> Build(LiveSessionHandler handler)
    {
        yield return Step("undo", "Revert the last change as one step", handler.UndoAsync);
        yield return Step("redo", "Reapply the last undone change", handler.RedoAsync);

        var labelArgument = new Argument<string?>("name")
        {
            Description = "A name for the transaction, shown in history",
            Arity = ArgumentArity.ZeroOrOne
        };
        var begin = new Command("begin", "Group the following commands into one undo step until commit or rollback") { labelArgument };
        begin.SetAction((parseResult, cancellationToken) =>
            RenderStepAsync(parseResult, handler.BeginAsync(parseResult.GetValue(labelArgument), cancellationToken)));
        yield return begin;

        yield return Step("commit", "Keep the open transaction's changes as one undo step", handler.CommitAsync);
        yield return Step("rollback", "Discard the open transaction's changes", handler.RollbackAsync);

        var status = new Command("status", "Show the model, unsaved changes, undo depth and open transaction");
        status.SetAction(parseResult => Render(parseResult, "status", handler.Status(), RenderStatus));
        yield return status;

        var history = new Command("history", "List the changes undo can revert and redo can reapply");
        history.SetAction(parseResult => Render(parseResult, "history", handler.History(), RenderHistory));
        yield return history;

        var discardOption = new Option<bool>("--discard")
        {
            Description = "Reload even though it discards unsaved changes"
        };
        var reload = new Command("reload", "Read the model's files again, after they changed outside the session; clears undo history") { discardOption };
        reload.SetAction((parseResult, cancellationToken) =>
            RenderStepAsync(parseResult, handler.ReloadAsync(parseResult.GetValue(discardOption), cancellationToken)));
        yield return reload;
    }

    private static Command Step(string name, string description, Func<CancellationToken, Task<TomixResult<SessionStepResult>>> run)
    {
        var command = new Command(name, description);
        command.SetAction((parseResult, cancellationToken) => RenderStepAsync(parseResult, run(cancellationToken)));
        return command;
    }

    private static async Task<int> RenderStepAsync(ParseResult parseResult, Task<TomixResult<SessionStepResult>> step)
        => Render(parseResult, parseResult.CommandResult.Command.Name, await step, RenderStep);

    private static int Render<T>(ParseResult parseResult, string name, TomixResult<T> result, Action<T> renderHuman)
    {
        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (!CommandOutput.TryValidateFormat(parseResult, format, name, OutputFormats.Text, OutputFormats.Json))
            return 2;

        return CommandOutput.Render(parseResult, result, format, renderHuman);
    }

    internal static void RenderStep(SessionStepResult result)
    {
        var subject = result.Label is { Length: > 0 } label ? label : result.Transaction;
        var line = result.Action switch
        {
            "undo" => $"Undone: {subject} ({Changes(result.Changes)})",
            "redo" => $"Redone: {subject} ({Changes(result.Changes)})",
            "begin" => $"Transaction {subject} open. Commands now group into one undo step until 'commit' or 'rollback'.",
            "commit" => $"Committed: {subject} ({Changes(result.Changes)})",
            "reload" => $"Reloaded from disk ({Changes(result.Changes)}); undo history cleared.",
            _ => $"Rolled back: {subject}"
        };
        AnsiConsole.MarkupLine(Styling.Success(line));
    }

    /// <summary>What <c>status</c> says when the session cannot reach its server (#351).</summary>
    internal static string Unreachable(string model)
        => (ModelReference.IsLocalInstanceEndpoint(model) ? "Power BI Desktop is not running" : "cannot be reached")
            + "; 'save -o <folder>' writes the changes to files";

    internal static void RenderStatus(SessionStatusResult status)
    {
        // A model on a server has no source path; name the endpoint instead.
        var rows = new List<(string, string)>
        {
            ("Model:", string.IsNullOrEmpty(status.Source) ? status.Model : status.Source),
            ("Unsaved changes:", status.Dirty ? "yes" : "no"),
        };
        if (status.State == SessionState.Stale)
            rows.Add(status.CanReload
                ? ("Files:", "changed outside the session; run 'reload' or 'save --force'")
                : ("Server:", "model changed outside the session; run 'save --force' or connect again"));
        if (status.SourceUnavailable)
            rows.Add(("Server:", Unreachable(status.Model)));
        rows.Add(("Undo steps:", Styling.Number(status.UndoSteps)));
        rows.Add(("Redo steps:", Styling.Number(status.RedoSteps)));
        rows.Add(("Transaction:", status.Transaction is { } open
            ? open.Label is { Length: > 0 } label ? $"{open.Id} ({label})" : open.Id
            : "none"));
        Styling.WriteKeyValues(rows);
    }

    internal static void RenderHistory(SessionHistoryResult history)
    {
        if (history.Steps.Count == 0)
        {
            StdErr.MarkupLine(Styling.Guidance("No changes yet."));
            return;
        }

        var table = Styling.NewTable("#", "Change", "Changes", "State");
        var number = 0;
        foreach (var step in history.Steps)
            table.AddRow(
                Styling.Number(++number),
                Styling.MarkupEscape(step.Label ?? step.Transaction),
                Styling.Number(step.Changes),
                step.Undone ? Styling.Muted("undone") : Styling.Success("applied"));
        AnsiConsole.Write(table);
    }

    private static string Changes(IReadOnlyList<ModelChange> changes)
        => changes.Count == 1 ? "1 change" : $"{changes.Count} changes";
}
