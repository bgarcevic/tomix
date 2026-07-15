using System.CommandLine;
using Spectre.Console;
using Tomix.App.IncrementalRefresh;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class IncrementalRefreshCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public IncrementalRefreshCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var command = new Command("incremental-refresh", "Configure incremental refresh policy on a table");
        command.Subcommands.Add(BuildApply());
        command.Subcommands.Add(BuildRm());
        command.Subcommands.Add(BuildSet());
        command.Subcommands.Add(BuildShow());
        return command;
    }

    private Command BuildShow()
    {
        var tableArgument = new Argument<string>("table") { Description = "Table name." };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var show = new Command("show", "Show incremental refresh policy")
        {
            tableArgument,
            modelArgument
        };

        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(formatValue, "incremental-refresh show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var reference = new ActiveModelResolver().ResolveReference(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Database),
                parseResult.GetValue(GlobalOptions.Server));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => new ShowRefreshPolicyHandler(_providers).HandleAsync(
                    new ShowRefreshPolicyRequest(reference, parseResult.GetValue(tableArgument) ?? ""),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), RenderShow);
        });

        return show;
    }

    private Command BuildSet()
    {
        var tableArgument = new Argument<string>("table") { Description = "Table name." };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var modeOption = new Option<string?>("--mode")
        {
            Description = "Policy mode: import (default) or hybrid (adds a DirectQuery partition for the newest data)"
        };
        modeOption.AcceptAmongIgnoreCase("import", "hybrid");
        var rollingWindowPeriodsOption = new Option<int?>("--rolling-window-periods")
        {
            Description = "How many periods of history to keep (the archive window)"
        };
        var rollingWindowGranularityOption = new Option<string?>("--rolling-window-granularity")
        {
            Description = "Granularity of the archive window: day, month, quarter, year"
        };
        rollingWindowGranularityOption.AcceptAmongIgnoreCase("day", "month", "quarter", "year");
        var incrementalPeriodsOption = new Option<int?>("--incremental-periods")
        {
            Description = "How many periods to refresh incrementally"
        };
        var incrementalGranularityOption = new Option<string?>("--incremental-granularity")
        {
            Description = "Granularity of the incremental window: day, month, quarter, year"
        };
        incrementalGranularityOption.AcceptAmongIgnoreCase("day", "month", "quarter", "year");
        var incrementalOffsetOption = new Option<int?>("--incremental-offset")
        {
            Description = "Periods to shift the window head from today (e.g. for future-dated data)"
        };
        var pollingExpressionOption = new Option<string?>("--polling-expression")
        {
            Description = "M expression polled per partition to detect data changes. Use '-' to read from stdin."
        };
        var pollingExpressionFileOption = new Option<string?>("--polling-expression-file")
        {
            Description = "Read the polling expression from a file"
        };
        var sourceExpressionOption = new Option<string?>("--source-expression")
        {
            Description = "M source query filtering on RangeStart/RangeEnd. Use '-' to read from stdin."
        };
        var sourceExpressionFileOption = new Option<string?>("--source-expression-file")
        {
            Description = "Read the source expression from a file"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Save despite validation errors; also lets --save-to overwrite an existing target"
        };
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location"
        };
        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save to a different path (implies --save)"
        };
        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };
        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var set = new Command("set", "Create or edit the incremental refresh policy on a table")
        {
            tableArgument,
            modelArgument,
            modeOption,
            rollingWindowPeriodsOption,
            rollingWindowGranularityOption,
            incrementalPeriodsOption,
            incrementalGranularityOption,
            incrementalOffsetOption,
            pollingExpressionOption,
            pollingExpressionFileOption,
            sourceExpressionOption,
            sourceExpressionFileOption,
            forceOption,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(formatValue, "incremental-refresh set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var reference = new ActiveModelResolver().ResolveReference(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Database),
                parseResult.GetValue(GlobalOptions.Server));
            var label = MutationSpinnerLabel.For(
                parseResult.GetValue(saveOption),
                parseResult.GetValue(saveToOption),
                parseResult.GetValue(stageOption),
                parseResult.GetValue(revertOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                label,
                () => new SetRefreshPolicyHandler(_providers).HandleAsync(
                    new SetRefreshPolicyRequest(
                        reference,
                        parseResult.GetValue(tableArgument) ?? "",
                        parseResult.GetValue(modeOption),
                        parseResult.GetValue(rollingWindowGranularityOption),
                        parseResult.GetValue(rollingWindowPeriodsOption),
                        parseResult.GetValue(incrementalGranularityOption),
                        parseResult.GetValue(incrementalPeriodsOption),
                        parseResult.GetValue(incrementalOffsetOption),
                        ResolveExpression(
                            parseResult.GetValue(pollingExpressionOption),
                            parseResult.GetValue(pollingExpressionFileOption)),
                        ResolveExpression(
                            parseResult.GetValue(sourceExpressionOption),
                            parseResult.GetValue(sourceExpressionFileOption)),
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), RenderSet);
        });

        return set;
    }

    private Command BuildRm()
    {
        var tableArgument = new Argument<string>("table") { Description = "Table name." };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var ifExistsOption = new Option<bool>("--if-exists")
        {
            Description = "Succeed silently if the table has no policy"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Allow --save-to to overwrite an existing target"
        };
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location"
        };
        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save to a different path (implies --save)"
        };
        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };
        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var rm = new Command("rm", "Remove the incremental refresh policy from a table")
        {
            tableArgument,
            modelArgument,
            ifExistsOption,
            forceOption,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        rm.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(formatValue, "incremental-refresh rm", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var table = parseResult.GetValue(tableArgument) ?? "";
            var revert = parseResult.GetValue(revertOption);

            if (!revert && !ConfirmationHelper.ConfirmOrAbort(
                "Remove incremental refresh policy from", table,
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            var reference = new ActiveModelResolver().ResolveReference(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Database),
                parseResult.GetValue(GlobalOptions.Server));
            var label = MutationSpinnerLabel.For(
                parseResult.GetValue(saveOption),
                parseResult.GetValue(saveToOption),
                parseResult.GetValue(stageOption),
                revert);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                label,
                () => new RemoveRefreshPolicyHandler(_providers).HandleAsync(
                    new RemoveRefreshPolicyRequest(
                        reference,
                        table,
                        parseResult.GetValue(ifExistsOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        revert,
                        parseResult.GetValue(noSyncOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), RenderRm);
        });

        return rm;
    }

    private Command BuildApply()
    {
        var tableArgument = new Argument<string>("table") { Description = "Table name." };
        var effectiveDateOption = new Option<DateOnly?>("--effective-date")
        {
            Description = "Date the policy is evaluated against (yyyy-MM-dd); defaults to today"
        };
        var noRefreshOption = new Option<bool>("--no-refresh")
        {
            Description = "Bootstrap: create/merge partition definitions without loading data"
        };
        var maxParallelismOption = new Option<int?>("--max-parallelism")
        {
            Description = "Maximum parallel refresh operations"
        };

        var apply = new Command("apply", "Apply the incremental refresh policy on a deployed model (generates partitions server-side)")
        {
            tableArgument,
            effectiveDateOption,
            noRefreshOption,
            maxParallelismOption
        };

        apply.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(formatValue, "incremental-refresh apply", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var table = parseResult.GetValue(tableArgument) ?? "";
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                $"Applying refresh policy for {table}...",
                () => new ApplyRefreshPolicyHandler(_providers).HandleAsync(
                    new ApplyRefreshPolicyRequest(
                        GlobalOptions.ModelValue(parseResult),
                        parseResult.GetValue(GlobalOptions.Server),
                        parseResult.GetValue(GlobalOptions.Database),
                        table,
                        parseResult.GetValue(effectiveDateOption),
                        Refresh: !parseResult.GetValue(noRefreshOption),
                        parseResult.GetValue(maxParallelismOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), RenderApply);
        });

        return apply;
    }

    /// <summary>Only resolve stdin/file when the option was actually supplied — an absent
    /// option must stay null (untouched on edit), not slurp redirected stdin.</summary>
    private static string? ResolveExpression(string? value, string? file)
        => value is null && string.IsNullOrWhiteSpace(file) ? null : InputValueResolver.Resolve(value, file);

    private static void RenderShow(RefreshPolicyInfo policy)
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

    private static void RenderSet(SetRefreshPolicyResult result)
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

    private static void RenderRm(RemoveRefreshPolicyResult result)
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

    private static void RenderApply(RefreshPolicyApplyResult result)
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
