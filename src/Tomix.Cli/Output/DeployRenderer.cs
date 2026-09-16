using Spectre.Console;
using Tomix.App.Deploy;
using Tomix.App.Diff;
using Tomix.Provider.Tom;

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

        // The banner names the source model the way its TMDL does (.platform displayName or
        // folder name), not as a raw path; the target database is already shown after the server.
        var modelName = ModelDisplayName.Resolve(tomName: null, sourcePath: source, fallback: result.Database);

        if (result.Status == "dry-run")
        {
            AnsiConsole.MarkupLine(Styling.Value($"Dry run: {modelName} to {result.Server} / {result.Database}"));

            if (result.CreatesDatabase == true)
            {
                AnsiConsole.MarkupLine(Styling.Success(
                    "Target database does not exist — this deploy creates it with the full source model."));
            }
            else if (result.Diff is not null)
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

        AnsiConsole.MarkupLine(Styling.Value(
            $"Deploying {modelName} to {result.Server} / {result.Database}..."));
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
