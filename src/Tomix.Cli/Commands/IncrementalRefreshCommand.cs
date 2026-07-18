using System.CommandLine;
using Tomix.App;
using Tomix.App.IncrementalRefresh;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class IncrementalRefreshCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public IncrementalRefreshCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
    }

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
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "incremental-refresh show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var reference = new ActiveModelResolver(_services.State).ResolveReference(
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

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), IncrementalRefreshRenderer.RenderShow);
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
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "incremental-refresh set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var reference = new ActiveModelResolver(_services.State).ResolveReference(
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
                () => new SetRefreshPolicyHandler(_providers, _services.Mutations).HandleAsync(
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

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), IncrementalRefreshRenderer.RenderSet);
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
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "incremental-refresh rm", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var table = parseResult.GetValue(tableArgument) ?? "";
            var revert = parseResult.GetValue(revertOption);

            if (!revert && !ConfirmationHelper.ConfirmOrAbort(
                "Remove incremental refresh policy from", table,
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            var reference = new ActiveModelResolver(_services.State).ResolveReference(
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
                () => new RemoveRefreshPolicyHandler(_providers, _services.Mutations).HandleAsync(
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

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), IncrementalRefreshRenderer.RenderRm);
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
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "incremental-refresh apply", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var table = parseResult.GetValue(tableArgument) ?? "";
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                $"Applying refresh policy for {table}...",
                () => new ApplyRefreshPolicyHandler(_providers, _services.LoadCurrentSession).HandleAsync(
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

            return CommandOutput.Render(result, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), IncrementalRefreshRenderer.RenderApply);
        });

        return apply;
    }

    /// <summary>Only resolve stdin/file when the option was actually supplied — an absent
    /// option must stay null (untouched on edit), not slurp redirected stdin.</summary>
    private static string? ResolveExpression(string? value, string? file)
        => value is null && string.IsNullOrWhiteSpace(file) ? null : InputValueResolver.Resolve(value, file);
}
