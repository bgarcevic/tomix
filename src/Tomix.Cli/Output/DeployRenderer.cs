using Spectre.Console;
using Tomix.App.Deploy;
using Tomix.App.Diff;
using Tomix.Core.Models;
using Tomix.Core.Rules;
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

        if (result.Status == "preview")
        {
            AnsiConsole.MarkupLine(Styling.Value($"Preview: {modelName} to {result.Server} / {result.Database}"));

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
                    DiffChangeRenderer.Render(change);
            }
            else if (result.DiffError is not null)
            {
                StdErr.MarkupLine(Styling.Warning($"Cannot preview changes: {result.DiffError}"));
            }

            return;
        }

        AnsiConsole.MarkupLine(Styling.Value(
            $"Deploying {modelName} to {result.Server} / {result.Database}..."));
        if (result.Status == ModelDeployResult.RolledBack)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Rolled back: the target is unchanged ({result.DurationMs}ms)"));
        else
            AnsiConsole.MarkupLine(Styling.Success(
                $"Deployed: {result.Status} ({result.DurationMs}ms)"));

        if (result.Validation is { } validation)
            RenderValidation(validation);
    }

    /// <summary>
    /// The post-deploy pass: each broken or incomplete object on its own line with the server's
    /// message, then a count of unprocessed objects — expected after a metadata deploy, so
    /// counted rather than listed.
    /// </summary>
    private static void RenderValidation(DeployValidation validation)
    {
        if (validation.Issues.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Success("Post-deploy check: all objects ready."));
            return;
        }

        if (validation.Errors + validation.Warnings > 0)
        {
            AnsiConsole.MarkupLine(Styling.Bold(
                $"Post-deploy check: {validation.Errors} error(s), {validation.Warnings} warning(s)"));
            foreach (var issue in validation.Issues.Where(issue => issue.Severity != RuleSeverity.Info))
            {
                var message = issue.Message is null ? string.Empty : $" — {issue.Message}";
                AnsiConsole.MarkupLine(
                    $"  {Styling.SeverityMarkup(issue.Severity.ToString())} "
                    + Styling.Value($"{issue.ObjectType} {issue.ObjectName}: {issue.State}{message}"));
            }
        }

        if (validation.Unprocessed > 0)
            AnsiConsole.MarkupLine(Styling.Guidance(
                $"{validation.Unprocessed} object(s) hold no data or need recalculation; run 'tx refresh' to process them."));
    }

    /// <summary>CI annotations (<c>--ci</c>) for the post-deploy errors and warnings.</summary>
    public static void EmitCi(string? ci, DeployModelResult result)
    {
        if (result.Validation is not { } validation)
            return;

        var annotations = validation.Issues
            .Where(issue => issue.Severity != RuleSeverity.Info)
            .Select(issue => new CiAnnotation(
                IsError: issue.Severity == RuleSeverity.Error,
                $"tx deploy {result.Database}: {issue.ObjectType} {issue.ObjectName} is {issue.State}"
                    + (issue.Message is null ? string.Empty : $": {issue.Message}")))
            .ToList();

        CiAnnotations.Emit(ci, annotations, Console.Error);
    }
}
