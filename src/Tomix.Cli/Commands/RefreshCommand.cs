using System.CommandLine;
using System.CommandLine.Parsing;
using Tomix.App.Refresh;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Commands;

internal sealed class RefreshCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public RefreshCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var typeOption = new Option<string?>("--type")
        {
            Description = "Refresh type: full, dataonly, automatic, calculate, clearvalues, defragment, add (default: automatic)"
        };

        var tableOption = new Option<string[]>("--table")
        {
            Description = "Specific table(s) to refresh. If omitted, refreshes the entire model. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var partitionOption = new Option<string[]>("--partition")
        {
            Description = "Specific partition(s) to refresh as TableName.PartitionName. Requires --table to be omitted. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var applyRefreshPolicyOption = new Option<bool?>("--apply-refresh-policy")
        {
            Description = "Apply incremental refresh policy (default: true). Set to false to skip policy-based partitioning.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var skipRefreshPolicyOption = new Option<bool>("--skip-refresh-policy")
        {
            Description = "Shorthand for --apply-refresh-policy false."
        };

        var effectiveDateOption = new Option<DateOnly?>("--effective-date")
        {
            Description = "Override the current date for incremental refresh policy evaluation (format: yyyy-MM-dd)."
        };

        var maxParallelismOption = new Option<int?>("--max-parallelism")
        {
            Description = "Maximum parallel refresh operations."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Output the TMSL script without executing it."
        };

        var noProgressOption = new Option<bool>("--no-progress")
        {
            Description = "Disable live progress tracking (for CI/piping)."
        };

        var traceOption = new Option<string?>("--trace")
        {
            Description = "Dump raw XMLA trace events. No value = stderr, path = write to log file.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("refresh", "Trigger a data refresh on a deployed model (--type full|auto|calculate|...)")
        {
            typeOption,
            tableOption,
            partitionOption,
            applyRefreshPolicyOption,
            skipRefreshPolicyOption,
            effectiveDateOption,
            maxParallelismOption,
            dryRunOption,
            noProgressOption,
            traceOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "refresh", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var type = parseResult.GetValue(typeOption) ?? "automatic";
            var tables = parseResult.GetValue(tableOption);
            var partitions = ParsePartitions(parseResult.GetValue(partitionOption), out var badPartition);
            if (partitions is null)
            {
                ErrorOutput.Write(
                    new[]
                    {
                        new TomixDiagnostic(
                            "TOMIX_REFRESH_BAD_PARTITION",
                            DiagnosticSeverity.Error,
                            $"Invalid --partition value '{badPartition}'. Expected TableName.PartitionName.",
                            Hint: "Example: --partition Sales.Internet")
                    },
                    parseResult.GetValue(GlobalOptions.ErrorFormat));
                return 2;
            }

            var applyPolicy = ResolveApplyPolicy(parseResult.GetValue(applyRefreshPolicyOption), parseResult.GetValue(skipRefreshPolicyOption));
            var effectiveDate = parseResult.GetValue(effectiveDateOption);
            var maxParallelism = parseResult.GetValue(maxParallelismOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var noProgress = parseResult.GetValue(noProgressOption);
            // ArgumentArity.ZeroOrOne surfaces both "absent" and "bare --trace" as null from GetValue.
            // Gate on GetResult so absent stays off, while bare --trace resolves to stderr ("-").
            var tracePath = parseResult.GetResult(traceOption) is null
                ? null
                : TraceWriter.ResolvePath(parseResult.GetValue(traceOption));

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult),
                    out var source,
                    out var recentExit))
                return recentExit;

            var request = new RefreshModelRequest(
                Model: source.Model,
                Server: source.Server,
                Database: source.Database,
                Auth: GlobalOptions.AuthValue(parseResult),
                RefreshType: type,
                Tables: tables is { Length: > 0 } ? tables : null,
                Partitions: partitions is { Count: > 0 } ? partitions : null,
                ApplyRefreshPolicy: applyPolicy,
                EffectiveDate: effectiveDate,
                MaxParallelism: maxParallelism,
                DryRun: dryRun,
                NoProgress: noProgress,
                TracePath: tracePath);

            // When --recent picked the target, resolve against that entry (not the active session)
            // so the refresh target and its workspace mirror come from the recent connection.
            var recentSession = RecentConnections.SessionSource(source);
            RefreshModelHandler CreateHandler() => recentSession is null
                ? new RefreshModelHandler(_providers)
                : new RefreshModelHandler(_providers, recentSession);

            // Progress + trace sinks: live spinner display via AnsiConsole.Status, plus optional --trace file/stderr.
            var suppressProgress = noProgress || quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format) || CliSpinner.ShouldSuppress();
            using var traceWriter = TraceWriter.Open(tracePath, quiet);
            try
            {
                // --dry-run never executes; no live display, just emit the script.
                if (dryRun)
                {
                    var dryResult = await CreateHandler()
                        .HandleAsync(request, progress: null, traceWriter, cancellationToken)
                        .ConfigureAwait(false);

                    if (dryResult.Success && dryResult.Data?.Script is not null)
                    {
                        if (OutputFormats.IsJson(format))
                            JsonOutput.Write(dryResult.Data);
                        else
                            RefreshRenderer.WriteTmsl(dryResult.Data.Script);
                    }
                    else
                    {
                        ErrorOutput.Write(dryResult.Diagnostics, parseResult.GetValue(GlobalOptions.ErrorFormat));
                    }
                    return dryResult.ExitCode == 0 && dryResult.Success ? 0 : dryResult.ExitCode;
                }

                TomixResult<RefreshModelResult> result;
                if (suppressProgress)
                {
                    result = await CreateHandler()
                        .HandleAsync(request, progress: null, traceWriter, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var display = new RefreshLiveDisplay();
                    result = await display.RunAsync(
                        BuildSpinnerLabel(request),
                        () => CreateHandler().HandleAsync(request, display.Progress, traceWriter, cancellationToken))
                        .ConfigureAwait(false);
                }

                return CommandOutput.Render(
                    result,
                    format,
                    RefreshRenderer.Render,
                    data => data,
                    RefreshRenderer.RenderCsv,
                    errorFormat: parseResult.GetValue(GlobalOptions.ErrorFormat));
            }
            finally
            {
                if (traceWriter is not null)
                    await traceWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });

        return command;
    }

    internal static IReadOnlyList<TablePartition>? ParsePartitions(string[]? raw, out string? badValue)
    {
        badValue = null;
        if (raw is null || raw.Length == 0)
            return Array.Empty<TablePartition>();

        var list = new List<TablePartition>(raw.Length);
        foreach (var value in raw)
        {
            var dot = value.IndexOf('.');
            if (dot <= 0 || dot >= value.Length - 1)
            {
                badValue = value;
                return null;
            }
            list.Add(new TablePartition(value[..dot], value[(dot + 1)..]));
        }
        return list;
    }

    internal static bool ResolveApplyPolicy(bool? explicitValue, bool skipFlag)
    {
        if (skipFlag) return false;
        if (explicitValue.HasValue) return explicitValue.Value;
        return true;
    }

    private static string BuildSpinnerLabel(RefreshModelRequest request)
    {
        var type = string.IsNullOrWhiteSpace(request.RefreshType) ? "automatic" : request.RefreshType;
        if (request.Partitions is { Count: > 0 })
            return $"Refreshing {request.Partitions.Count} partition(s) ({type})...";
        if (request.Tables is { Count: > 0 })
            return $"Refreshing {request.Tables.Count} table(s) ({type})...";
        return $"Refreshing model ({type})...";
    }
}
