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

    private readonly CliStateStore _state;
    private readonly Func<CliConnectionState?> _loadCurrentSession;

    public RefreshCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        Func<CliConnectionState?> loadCurrentSession)
    {
        _providers = providers;
        _state = state;
        _loadCurrentSession = loadCurrentSession;
    }

    public Command Build()
    {
        // "refresh-type" rather than "type": --type means an object kind everywhere else, and
        // this option selects the refresh operation instead (docs/cli-ux-guidelines.md).
        var typeOption = new Option<string?>("--refresh-type")
        {
            Description = "Kind of refresh to run: full, dataonly, automatic, calculate, clearvalues, defragment, or add (default: automatic)"
        };

        var tableOption = new Option<string[]>("--table")
        {
            Description = "Refresh only these tables; the whole model is refreshed when omitted. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var partitionOption = new Option<string[]>("--partition")
        {
            Description = "Refresh only these partitions, written as TableName.PartitionName; leave --table unset. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var applyRefreshPolicyOption = new Option<bool?>("--apply-refresh-policy")
        {
            Description = "Let an incremental refresh policy choose the partitions (default: true). Pass false to refresh without it.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var skipRefreshPolicyOption = new Option<bool>("--skip-refresh-policy")
        {
            Description = "Shorthand for --apply-refresh-policy false"
        };

        var effectiveDateOption = new Option<DateOnly?>("--effective-date")
        {
            Description = "Evaluate incremental refresh policies as if today were this date (yyyy-MM-dd)"
        };

        var maxParallelismOption = new Option<int?>("--max-parallelism")
        {
            Description = "Maximum parallel refresh operations"
        };

        var policyOnlyOption = new Option<bool>("--policy-only")
        {
            Description = "Apply one table's saved refresh policy without loading data; may remove expired partitions. Requires --table."
        };

        var noProgressOption = new Option<bool>("--no-progress")
        {
            Description = "Turn off live progress tracking (useful in CI and when piping)"
        };

        var traceOption = new Option<string?>("--trace")
        {
            Description = "Write raw XMLA trace events: with no value to stderr, with a value to that log file",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("refresh", "Refresh data on a deployed model")
        {
            typeOption,
            tableOption,
            partitionOption,
            applyRefreshPolicyOption,
            skipRefreshPolicyOption,
            effectiveDateOption,
            maxParallelismOption,
            policyOnlyOption,
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
                    GlobalOptions.ErrorFormatValue(parseResult, format));
                return 2;
            }

            var applyPolicy = ResolveApplyPolicy(parseResult.GetValue(applyRefreshPolicyOption), parseResult.GetValue(skipRefreshPolicyOption));
            var effectiveDate = parseResult.GetValue(effectiveDateOption);
            var maxParallelism = parseResult.GetValue(maxParallelismOption);
            var noProgress = parseResult.GetValue(noProgressOption);
            // ArgumentArity.ZeroOrOne surfaces both "absent" and "bare --trace" as null from GetValue.
            // Gate on GetResult so absent stays off, while bare --trace resolves to stderr ("-").
            var tracePath = parseResult.GetResult(traceOption) is null
                ? null
                : TraceWriter.ResolvePath(parseResult.GetValue(traceOption));

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult),
                    _state,
                    out var source,
                    out var recentExit))
                return recentExit;

            // The handler resolves the same triple internally; pre-resolve only to name the
            // target on stderr when it came implicitly from the active connection.
            if (source.IsImplicit)
                ConnectionBanner.Announce(
                    parseResult,
                    RecentConnections.CreateResolver(source, _state)
                        .ResolveReference(source.Model, source.Database, source.Server),
                    _state.LoadCurrentSession());

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
                Preview: false,
                NoProgress: noProgress,
                TracePath: tracePath,
                PolicyOnly: parseResult.GetValue(policyOnlyOption),
                RefreshTypeExplicit: parseResult.GetResult(typeOption) is not null);

            var policyValidation = request.PolicyOnly && parseResult.GetResult(partitionOption) is not null
                ? "--policy-only cannot be combined with --partition."
                : RefreshModelHandler.ValidatePolicyOnly(request);
            if (policyValidation is { } policyError)
            {
                ErrorOutput.Write([new TomixDiagnostic("TOMIX_REFRESH_POLICY_OPTIONS_CONFLICT",
                    DiagnosticSeverity.Error, policyError)], GlobalOptions.ErrorFormatValue(parseResult, format));
                return 2;
            }

            // When --recent picked the target, resolve against that entry (not the active session)
            // so the refresh target and its workspace mirror come from the recent connection.
            var recentSession = RecentConnections.SessionSource(source);
            RefreshModelHandler CreateHandler() => recentSession is null
                ? new RefreshModelHandler(_providers, _loadCurrentSession)
                : new RefreshModelHandler(_providers, recentSession);

            // Progress + trace sinks: live spinner display via AnsiConsole.Status, plus optional --trace file/stderr.
            var suppressProgress = noProgress || quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format) || CliSpinner.ShouldSuppress();
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, format);
            using var traceWriter = TraceWriter.Open(tracePath, quiet);
            RefreshOperation? operation = null;
            try
            {
                // Partition-risky variants preview first: clearvalues wipes partition data, and
                // bypassing or overriding the incremental-refresh policy (or applying it without
                // loading) can rebuild or drop historical partitions. They show the TMSL the refresh
                // would send (or the validated policy-only operation), then confirm. Routine
                // refreshes run straight away. The preview never executes, so it has no live display.
                var partitionRisky =
                    string.Equals(type, "clearvalues", StringComparison.OrdinalIgnoreCase)
                    || !applyPolicy
                    || effectiveDate is not null
                    || request.PolicyOnly;
                // A routine refresh connects inside its live display. A previewed one keeps the
                // preview's session open and refreshes on it, so it connects only once.
                Func<IProgress<RefreshProgress>?, Task<TomixResult<RefreshModelResult>>> run =
                    progress => CreateHandler().HandleAsync(request, progress, traceWriter, cancellationToken);
                if (partitionRisky && PreviewGate.PreviewFirst(parseResult))
                {
                    operation = await CreateHandler().OpenAsync(request, cancellationToken).ConfigureAwait(false);
                    var preview = await operation.PreviewAsync(cancellationToken).ConfigureAwait(false);
                    var previewExit = RenderPreview(preview, format, errorFormat);
                    if (!preview.Success || previewExit != 0)
                        return previewExit;

                    var decision = PreviewGate.Decide(
                        request.PolicyOnly ? "Apply refresh policy without loading data (may remove expired partitions) to" : "Refresh",
                        $"{preview.Data!.Database ?? "model"} on {preview.Data.Server}" + (request.PolicyOnly ? "" : $" ({type})"),
                        parseResult,
                        format);
                    if (decision != PreviewDecision.Apply)
                        return PreviewGate.ExitCode(decision);

                    var confirmed = operation;
                    run = progress => confirmed.ApplyAsync(progress, traceWriter, cancellationToken);
                }

                if (request.PolicyOnly)
                {
                    var policyResult = await run(null).ConfigureAwait(false);
                    return CommandOutput.Render(policyResult, format, RefreshRenderer.Render, data => data,
                        RefreshRenderer.RenderCsv, errorFormat: errorFormat);
                }

                TomixResult<RefreshModelResult> result;
                if (suppressProgress)
                {
                    result = await run(null).ConfigureAwait(false);
                }
                else
                {
                    var display = new RefreshLiveDisplay();
                    result = await display.RunAsync(BuildSpinnerLabel(request), () => run(display.Progress))
                        .ConfigureAwait(false);
                }

                return CommandOutput.Render(
                    result,
                    format,
                    RefreshRenderer.Render,
                    data => data,
                    RefreshRenderer.RenderCsv,
                    errorFormat: errorFormat);
            }
            finally
            {
                if (operation is not null)
                    await operation.DisposeAsync().ConfigureAwait(false);
                if (traceWriter is not null)
                    await traceWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });

        return command;
    }

    /// <summary>
    /// Renders a refresh preview: the validated operation summary for --policy-only, otherwise
    /// the TMSL script (raw in text output, inside the envelope under JSON).
    /// </summary>
    private static int RenderPreview(TomixResult<RefreshModelResult> preview, string format, string? errorFormat)
    {
        if (preview.Data?.PolicyPreview is not null)
            return CommandOutput.Render(preview, format, RefreshRenderer.Render, data => data,
                RefreshRenderer.RenderCsv, errorFormat: errorFormat);

        if (!preview.Success || preview.Data?.Script is null)
        {
            ErrorOutput.Write(preview.Diagnostics, errorFormat);
            return preview.ExitCode == 0 ? 1 : preview.ExitCode;
        }

        if (OutputFormats.IsJson(format))
            JsonOutput.Write(new CommandEnvelope<object>(preview.Data, preview.Diagnostics));
        else
            RefreshRenderer.WriteTmsl(preview.Data.Script);
        return 0;
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
