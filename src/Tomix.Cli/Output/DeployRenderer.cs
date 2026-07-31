using Spectre.Console;
using Tomix.App.Deploy;
using Tomix.App.Diff;

namespace Tomix.Cli.Output;

internal static class DeployRenderer
{
    public static void Render(DeployModelResult result, string source)
    {
        if (result.Status == "script")
        {
            AnsiConsole.MarkupLine(Styling.KeyValue("Source:", source));
            AnsiConsole.MarkupLine(Styling.KeyValue("Script:", result.ScriptPath ?? ""));
            return;
        }

        if (result.Status == "dry-run")
        {
            var name = string.IsNullOrWhiteSpace(result.Database) ? source : result.Database;
            AnsiConsole.MarkupLine(Styling.Value($"Dry run: {name} to {result.Server} / {result.Database}"));

            if (result.Diff is not null)
            {
                if (!result.Diff.HasChanges)
                {
                    AnsiConsole.MarkupLine(Styling.Success("No changes — local and remote are identical."));
                    return;
                }

                var summary = result.Diff.Summary;
                AnsiConsole.MarkupLine(Styling.Bold(
                    $"{summary.Added} added, {summary.Removed} removed, {summary.Modified} modified"));
                AnsiConsole.WriteLine();

                foreach (var change in result.Diff.Changes)
                    RenderDiffChange(change);
            }
            else if (result.DiffError is not null)
            {
                AnsiConsole.MarkupLine(Styling.Warning(
                    $"Diff unavailable: {Styling.MarkupEscape(result.DiffError)}"));
                AnsiConsole.MarkupLine(Styling.Muted("Showing deploy plan only."));
            }
            else
            {
                AnsiConsole.MarkupLine(Styling.Muted(
                    "No remote target specified — showing deploy plan only."));
            }

            return;
        }

        var deployName = string.IsNullOrWhiteSpace(result.Database) ? source : result.Database;
        AnsiConsole.MarkupLine(Styling.Value(
            $"Deploying {deployName} to {result.Server} / {result.Database}..."));
        AnsiConsole.MarkupLine(Styling.Success(
            $"Deployed: {result.Status} ({result.DurationMs}ms)"));
    }

    private static void RenderDiffChange(DiffChange change)
    {
        switch (change.Action)
        {
            case "added":
                AnsiConsole.MarkupLine(
                    $"  {Styling.Success("+")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "removed":
                AnsiConsole.MarkupLine(
                    $"  {Styling.Error("-")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "modified":
                AnsiConsole.MarkupLine(
                    $"  {Styling.Warning("~")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                AnsiConsole.MarkupLine($"    {Styling.Error($"- {change.OldValue}")}");
                AnsiConsole.MarkupLine($"    {Styling.Success($"+ {change.NewValue}")}");
                break;
        }
    }
}
