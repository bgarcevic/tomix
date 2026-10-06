using Tomix.App.Connect;
using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Refresh;

/// <summary>
/// Resolves the refresh target (primary if remote, else the remote workspace-mode secondary, else
/// the Power BI Desktop instance that has the local model's PBIP open), opens a refresh-capable
/// session, and runs the refresh. Mirrors <see cref="Deploy.DeployModelHandler"/>.
/// All console/Spectre concerns stay in the CLI: progress and trace writers are injected.
/// </summary>
public sealed class RefreshModelHandler
{
    private static readonly string[] ValidRefreshTypes =
        ["full", "dataonly", "dataOnly", "automatic", "auto", "calculate", "clearvalues", "clearValues", "defragment", "add"];

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;
    private readonly Func<string, PowerBiDesktopInstance?> _findDesktop;

    /// <param name="findDesktop">
    /// The running Power BI Desktop instance with a local model's files open; defaults to
    /// <see cref="PowerBiDesktopProjects.FindOpening(string)"/>.
    /// </param>
    public RefreshModelHandler(
        IEnumerable<IModelProvider> providers,
        Func<CliConnectionState?> resolveSession,
        Func<string, PowerBiDesktopInstance?>? findDesktop = null)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
        _findDesktop = findDesktop ?? PowerBiDesktopProjects.FindOpening;
    }

    /// <summary>Previews (<see cref="RefreshModelRequest.Preview"/>) or runs one refresh.</summary>
    /// <param name="progress">Optional progress channel (CLI-owned; null when --no-progress or non-TTY).</param>
    /// <param name="traceWriter">Optional trace sink for --trace (null=off, stderr/file owned by the CLI).</param>
    public async Task<TomixResult<RefreshModelResult>> HandleAsync(
        RefreshModelRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        await using var operation = await OpenAsync(request, cancellationToken).ConfigureAwait(false);
        return request.Preview
            ? await operation.PreviewAsync(cancellationToken).ConfigureAwait(false)
            : await operation.ApplyAsync(progress, traceWriter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the request, resolves the remote target, and opens one session on it. The
    /// returned operation previews and applies on that session, so a preview-then-confirm flow
    /// connects once. A failure here is carried by the operation and returned by whichever of its
    /// methods is called.
    /// </summary>
    public async Task<RefreshOperation> OpenAsync(RefreshModelRequest request, CancellationToken cancellationToken)
    {
        if (ValidatePolicyOnly(request) is { } policyError)
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_POLICY_OPTIONS_CONFLICT", policyError, exitCode: 2));

        var typeValidation = ValidateRefreshType(request.RefreshType);
        if (typeValidation is not null)
            return RefreshOperation.Failed(typeValidation);

        if (request.Partitions is { Count: > 0 } && request.Tables is { Count: > 0 })
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_TABLE_PARTITION_CONFLICT",
                "Pass either --table or --partition, not both. --partition implies its own table scope (Table.Partition).",
                exitCode: 2));

        if (request.Partitions is { Count: > 0 } && request.Partitions.Any(p => string.IsNullOrWhiteSpace(p.Partition) || string.IsNullOrWhiteSpace(p.Table)))
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_BAD_PARTITION",
                "--partition values must be formatted as TableName.PartitionName.",
                exitCode: 2,
                hint: "Example: --partition Sales.Internet"));

        var resolver = new ActiveModelResolver(_resolveSession);
        var target = ResolveTarget(request, resolver);
        IReadOnlyList<TomixDiagnostic> notices = [];
        if (target is null)
        {
            // Files cannot be refreshed, but the engine of a Power BI Desktop that has them open
            // can: refresh there, as Tabular Editor does when Desktop launches it.
            var local = resolver.ResolveReference(request.Model, request.Database, request.Server);
            if (local.IsLocalPath && _findDesktop(local.Value) is { } desktop)
            {
                target = ModelReference.Remote(desktop.Endpoint);
                notices = [DesktopNotice(local.Value, desktop)];
            }
        }

        if (target is null)
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                "No deployed model to refresh: files cannot be refreshed, no workspace-mode secondary is set, and no running Power BI Desktop has these files open.",
                exitCode: 2,
                hint: "Open the PBIP in Power BI Desktop, use 'tx connect -s <workspace> -d <model>' or pass -s/-d explicitly, or set up workspace mode with 'tx connect --workspace <endpoint>'."));

        if (!target.IsRemote)
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                $"Resolved target '{target.Value}' is not a remote endpoint. Only deployed models can be refreshed.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model> to target a deployed model."));

        var provider = _providers.ResolveSingleProvider(target);
        if (provider is null)
            return RefreshOperation.Failed(TomixResult<RefreshModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2));

        IModelSession session;
        try
        {
            session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RefreshOperation.Failed(MapFailure(ex, target, request.PolicyOnly));
        }

        if (request.PolicyOnly
            ? session is not (IRefreshPolicyApplySession and IRefreshPolicyMutationSession)
            : session is not IModelRefreshSession)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return RefreshOperation.Failed(request.PolicyOnly
                ? TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_POLICY_UNSUPPORTED",
                    "Provider does not support inspecting and applying refresh policies on deployed models.", exitCode: 2)
                : TomixResult<RefreshModelResult>.Fail(
                    "TOMIX_REFRESH_UNSUPPORTED",
                    $"Provider session does not support refresh: {target.Value}",
                    exitCode: 2,
                    hint: "Refresh is only supported on deployed models connected via XMLA (-s <workspace> -d <model>)."));
        }

        return new RefreshOperation(session, target, request, notices);
    }

    private static TomixDiagnostic DesktopNotice(string path, PowerBiDesktopInstance desktop)
        => new(
            "TOMIX_REFRESH_IN_DESKTOP",
            DiagnosticSeverity.Info,
            $"'{path}' is open in Power BI Desktop{(desktop.ReportName is { } name ? $" ('{name}')" : "")}, so the model there ({desktop.Endpoint}) was refreshed.",
            Hint: "Save in Power BI Desktop to keep the refreshed data.");

    /// <summary>Maps a failure while connecting, previewing, or refreshing to its diagnostic.</summary>
    internal static TomixResult<RefreshModelResult> MapFailure(Exception ex, ModelReference target, bool policyOnly)
    {
        switch (ex)
        {
            case ModelConnectionException connection:
                return ProviderConnectionGuard.ConnectionFailure<RefreshModelResult>(target, connection);
            case AuthenticationRequiredException auth:
                return AuthFail(auth);
        }

        if (policyOnly)
            return ex switch
            {
                ObjectNotFoundException notFound =>
                    TomixResult<RefreshModelResult>.Fail("TOMIX_OBJECT_NOT_FOUND", notFound.Message, hint: notFound.Hint),
                RefreshPolicyNotFoundException =>
                    TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_POLICY_NOT_FOUND", ex.Message),
                _ => TomixResult<RefreshModelResult>.Fail("TOMIX_REFRESH_POLICY_APPLY_FAILED",
                    $"Applying the refresh policy failed: {ex.InnerException?.Message ?? ex.Message}", exitCode: 1),
            };

        if (ex is InvalidOperationException)
            return TomixResult<RefreshModelResult>.Fail(
                "TOMIX_REFRESH_FAILED",
                ex.Message,
                exitCode: 1,
                hint: "Verify the table/partition names and that you have refresh permissions on the dataset.");

        return TomixResult<RefreshModelResult>.Fail(
            "TOMIX_REFRESH_FAILED",
            $"Refresh of '{target.Database ?? target.Value}' failed: {ex.InnerException?.Message ?? ex.Message}",
            exitCode: 1);
    }

    /// <summary>Validates policy-only flags before confirmation or any connection/trace side effects.</summary>
    public static string? ValidatePolicyOnly(RefreshModelRequest request)
    {
        if (!request.PolicyOnly)
            return null;
        if (request.Tables is not { Count: 1 } || string.IsNullOrWhiteSpace(request.Tables[0]))
            return "--policy-only requires exactly one explicit --table.";
        if (request.Partitions is { Count: > 0 } || request.RefreshTypeExplicit ||
            !string.Equals(request.RefreshType, "automatic", StringComparison.OrdinalIgnoreCase) ||
            !request.ApplyRefreshPolicy || request.TracePath is not null)
            return "--policy-only cannot be combined with --partition, --refresh-type, --trace, --skip-refresh-policy, or --apply-refresh-policy false.";
        if (request.MaxParallelism is <= 0)
            return "--max-parallelism must be positive.";
        return null;
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

    internal static string NormalizeType(string refreshType)
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
    /// Public so the CLI confirmation gate resolves the exact target this handler would use.
    /// </summary>
    public static ModelReference? ResolveTarget(
        RefreshModelRequest request,
        ActiveModelResolver resolver)
    {
        var primary = resolver.ResolveReference(request.Model, request.Database, request.Server);
        if (primary.IsRemote)
            return primary;

        // The mirror fallback only applies when the local primary IS the session's primary
        // model — an explicit unrelated local source must not refresh the session's mirror.
        var secondary = resolver.ResolveSyncTarget(primary);
        if (secondary is null || !secondary.IsRemote)
            return null;

        return secondary;
    }

    private static TomixResult<RefreshModelResult> AuthFail(AuthenticationRequiredException ex)
        => TomixResult<RefreshModelResult>.Fail(
            "TOMIX_AUTH_REQUIRED",
            ex.Message,
            exitCode: 1,
            hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
}
