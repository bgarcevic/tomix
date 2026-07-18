using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Refresh;

/// <summary>
/// Resolves the refresh target (primary if remote, else the remote workspace-mode secondary),
/// opens a refresh-capable session, and runs the refresh. Mirrors <see cref="Deploy.DeployModelHandler"/>.
/// All console/Spectre concerns stay in the CLI: progress and trace writers are injected.
/// </summary>
public sealed class RefreshModelHandler
{
    private static readonly string[] ValidRefreshTypes =
        ["full", "dataonly", "dataOnly", "automatic", "auto", "calculate", "clearvalues", "clearValues", "defragment", "add"];

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;

    public RefreshModelHandler(IEnumerable<IModelProvider> providers)
        : this(providers, () => new CliStateStore().LoadCurrentSession())
    {
    }

    public RefreshModelHandler(IEnumerable<IModelProvider> providers, Func<CliConnectionState?> resolveSession)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
    }

    /// <param name="progress">Optional progress channel (CLI-owned; null when --no-progress or non-TTY).</param>
    /// <param name="traceWriter">Optional trace sink for --trace (null=off, stderr/file owned by the CLI).</param>
    public async Task<TomixResult<RefreshModelResult>> HandleAsync(
        RefreshModelRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        var typeValidation = ValidateRefreshType(request.RefreshType);
        if (typeValidation is not null)
            return typeValidation;

        if (request.Partitions is { Count: > 0 } && request.Tables is { Count: > 0 })
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_TABLE_PARTITION_CONFLICT",
                "Pass either --table or --partition, not both. --partition implies its own table scope (Table.Partition).",
                exitCode: 2);

        if (request.Partitions is { Count: > 0 } && request.Partitions.Any(p => string.IsNullOrWhiteSpace(p.Partition) || string.IsNullOrWhiteSpace(p.Table)))
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_BAD_PARTITION",
                "--partition values must be formatted as TableName.PartitionName.",
                exitCode: 2,
                hint: "Example: --partition Sales.Internet");

        var target = ResolveTarget(request);
        if (target is null)
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                "No remote connection to refresh. The default connection is local and no remote workspace-mode secondary is set.",
                exitCode: 2,
                hint: "Use 'tx connect -s <workspace> -d <model>' or pass -s/-d explicitly, or set up workspace mode with 'tx connect --workspace <endpoint>'.");

        if (!target.IsRemote)
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                $"Resolved target '{target.Value}' is not a remote endpoint. Only deployed models can be refreshed.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model> to target a deployed model.");

        var provider = _providers.ResolveSingle(target);
        if (provider is null)
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2);

        var sessionRequest = new ModelRefreshRequest(
            Database: target.Database,
            RefreshType: request.RefreshType,
            Tables: request.Tables,
            Partitions: request.Partitions,
            ApplyRefreshPolicy: request.ApplyRefreshPolicy,
            EffectiveDate: request.EffectiveDate,
            MaxParallelism: request.MaxParallelism);

        // --dry-run: emit TMSL without executing. We still need to open the session so the
        // provider can resolve the live model for partition validation, but never call RefreshAsync.
        if (request.DryRun)
        {
            try
            {
                await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
                if (session is not IModelRefreshSession refresher)
                    return TomixResult<RefreshModelResult>.Fail(
                        "TOMIX_REFRESH_UNSUPPORTED",
                        $"Provider cannot generate refresh script for: {target.Value}",
                        exitCode: 2);

                var script = refresher.GenerateRefreshScript(sessionRequest);
                return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(
                    target.Value, target.Database, NormalizeType(request.RefreshType), 0, Array.Empty<RefreshTableResult>(), null, script));
            }
            catch (AuthenticationRequiredException ex)
            {
                return AuthFail(ex);
            }
            catch (InvalidOperationException ex)
            {
                return TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_FAILED", ex.Message, exitCode: 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_FAILED", ex.Message, exitCode: 1);
            }
        }

        try
        {
            await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (session is not IModelRefreshSession refresher)
                return TomixResult<RefreshModelResult>.Fail(
                    "TOMIX_REFRESH_UNSUPPORTED",
                    $"Provider session does not support refresh: {target.Value}",
                    exitCode: 2,
                    hint: "Refresh is only supported on deployed models connected via XMLA (-s <workspace> -d <model>).");

            var result = await refresher.RefreshAsync(sessionRequest, progress, traceWriter, cancellationToken).ConfigureAwait(false);

            return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(
                result.Server, result.Database, result.RefreshType, result.DurationMs, result.Tables, result.Totals, Script: null));
        }
        catch (AuthenticationRequiredException ex)
        {
            return AuthFail(ex);
        }
        catch (InvalidOperationException ex)
        {
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_FAILED",
                ex.Message,
                exitCode: 1,
                hint: "Verify the table/partition names and that you have refresh permissions on the dataset.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_FAILED",
                $"Refresh of '{target.Database ?? target.Value}' failed: {msg}",
                exitCode: 1);
        }
    }

    private static TomixResult<RefreshModelResult>? ValidateRefreshType(string? refreshType)
    {
        var value = string.IsNullOrWhiteSpace(refreshType) ? "automatic" : refreshType;
        if (ValidRefreshTypes.Contains(value, StringComparer.OrdinalIgnoreCase))
            return null;
        return TomixResult<RefreshModelResult>.Fail(
            "TOMIX_REFRESH_BAD_TYPE",
            $"Unknown refresh type '{value}'. Valid: full, dataonly, automatic, calculate, clearvalues, defragment, add.",
            exitCode: 2);
    }

    private static string NormalizeType(string refreshType)
    {
        var value = string.IsNullOrWhiteSpace(refreshType) ? "automatic" : refreshType;
        return value.ToLowerInvariant() switch
        {
            "auto" => "automatic",
            "dataonly" => "dataOnly",
            "clearvalues" => "clearValues",
            var v when v is "full" or "automatic" or "calculate" or "defragment" or "add" => v,
            _ => value
        };
    }

    /// <summary>
    /// Pure target resolution: primary reference if remote, otherwise the workspace-mode
    /// secondary when it is remote, otherwise null. Honors explicit --server/--database.
    /// </summary>
    internal static ModelReference? ResolveTarget(
        RefreshModelRequest request,
        ActiveModelResolver resolver)
    {
        var primary = resolver.ResolveReference(request.Model, request.Database, request.Server);
        if (primary.IsRemote)
            return primary;

        var secondary = resolver.ResolveSyncTarget();
        if (secondary is null || !secondary.IsRemote)
            return null;

        return secondary;
    }

    private ModelReference? ResolveTarget(RefreshModelRequest request)
        => ResolveTarget(request, new ActiveModelResolver(_resolveSession));

    private static TomixResult<RefreshModelResult> AuthFail(AuthenticationRequiredException ex)
        => TomixResult<RefreshModelResult>.Fail(
            "TOMIX_AUTH_REQUIRED",
            ex.Message,
            exitCode: 1,
            hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
}
