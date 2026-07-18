using Spectre.Console;
using Tomix.App.IncrementalRefresh;
using Tomix.Core.Models;

namespace Tomix.Cli.Output;

/// <summary>
/// Text rendering for the <c>incremental-refresh</c> subcommands
/// (<c>show</c>, <c>set</c>, <c>rm</c>, <c>apply</c>).
/// </summary>
internal static class IncrementalRefreshRenderer
{
    public static void RenderShow(RefreshPolicyInfo policy)
    {
        Console.WriteLine($"{policy.Table} (RefreshPolicy)");
        Console.WriteLine($"mode: {policy.Mode}");
        Console.WriteLine($"rollingWindow: {policy.RollingWindowPeriods} {policy.RollingWindowGranularity}");
        Console.WriteLine($"incremental: {policy.IncrementalPeriods} {policy.IncrementalGranularity}");
        Console.WriteLine($"incrementalOffset: {policy.IncrementalOffset}");
        Console.WriteLine($"pollingExpression: {(string.IsNullOrEmpty(policy.PollingExpression) ? "(none)" : policy.PollingExpression)}");
        Console.WriteLine($"sourceExpression: {policy.SourceExpression}");
        Console.WriteLine($"policyPartitions: {(policy.PolicyPartitions.Count == 0 ? "(none)" : string.Join(", ", policy.PolicyPartitions))}");

        RenderIssues(policy.Issues);
    }

    public static void RenderSet(SetRefreshPolicyResult result)
    {
        if (result.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success($"Reverted staged changes for {result.Table}."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success(
            $"{(result.Created ? "Created" : "Updated")} incremental refresh policy: {result.Table}"));

        if (result.Policy is { } policy)
            AnsiConsole.MarkupLine(Styling.Muted(
                $"{policy.Mode}: keep {policy.RollingWindowPeriods} {policy.RollingWindowGranularity}, "
                + $"refresh {policy.IncrementalPeriods} {policy.IncrementalGranularity}"));

        if (result.CreatedExpressions is { Count: > 0 } expressions)
            foreach (var name in expressions)
                AnsiConsole.MarkupLine(Styling.Guidance($"Created shared expression '{name}' (DateTime parameter)."));

        if (result.Issues is { Count: > 0 } issues)
        {
            // A saved result carrying errors means --force overrode blocking validation; call
            // that out so the user sees what they bypassed rather than a bare success.
            var errorCount = issues.Count(i => i.IsError);
            if (errorCount > 0)
                AnsiConsole.MarkupLine(Styling.Warning(
                    $"Saved despite {errorCount} validation error(s) (--force)."));
            RenderIssues(issues);
        }

        if (result.Staged == true)
            AnsiConsole.MarkupLine(Styling.Guidance("Staged. Run 'tx stage commit' to promote."));
        else if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist or --stage to stage."));
        else
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {result.SyncTarget}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(result.SyncWarning));
    }

    public static void RenderRm(RemoveRefreshPolicyResult result)
    {
        if (result.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success("Reverted."));
            return;
        }

        if (result.Removed is false)
        {
            // The only Changed=false path is --if-exists on a table without a policy.
            if (result.Reason == "not_found")
                AnsiConsole.MarkupLine(Styling.Success("No policy found (nothing removed)"));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Removed incremental refresh policy: {result.Removed}"));

        if (result.RemainingPolicyPartitions is { Count: > 0 } partitions)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"The policy-generated partitions remain on the table: {string.Join(", ", partitions)}. "
                + "Remove them with 'tx rm' or leave them as regular partitions."));

        if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist."));
        else if (result.Saved is not null)
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {result.SyncTarget}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(result.SyncWarning));
    }

    public static void RenderApply(RefreshPolicyApplyResult result)
    {
        AnsiConsole.MarkupLine(Styling.Success(
            $"Applied refresh policy: {result.Table} on {result.Database} (effective {result.EffectiveDate:yyyy-MM-dd})"));

        foreach (var operation in result.Operations)
            AnsiConsole.MarkupLine(Styling.Muted(operation));
        if (result.Operations.Count == 0)
            AnsiConsole.MarkupLine(Styling.Muted("Partitions already match the policy; no changes."));

        if (!result.Refreshed)
            AnsiConsole.MarkupLine(Styling.Guidance(
                $"Partitions created without data (bootstrap). Run 'tx refresh --table {result.Table}' to load."));
    }

    private static void RenderIssues(IReadOnlyList<RefreshPolicyIssue> issues)
    {
        foreach (var issue in issues)
        {
            AnsiConsole.MarkupLine(issue.IsError
                ? Styling.Error($"error [{issue.Code}]: {issue.Message}")
                : Styling.Warning($"warning [{issue.Code}]: {issue.Message}"));
        }
    }
}
