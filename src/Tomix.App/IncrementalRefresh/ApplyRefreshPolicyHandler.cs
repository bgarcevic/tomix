using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.IncrementalRefresh;

/// <summary>
/// Applies a table's incremental refresh policy on a deployed model. Mirrors
/// <see cref="Refresh.RefreshModelHandler"/>: the target must be remote (XMLA) — the server
/// owns partition generation — so local/file models fail with a clear error.
/// </summary>
public sealed class ApplyRefreshPolicyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;

    public ApplyRefreshPolicyHandler(IEnumerable<IModelProvider> providers)
        : this(providers, () => new CliStateStore().LoadCurrentSession())
    {
    }

    public ApplyRefreshPolicyHandler(IEnumerable<IModelProvider> providers, Func<CliConnectionState?> resolveSession)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
    }

    public async Task<TomixResult<RefreshPolicyApplyResult>> HandleAsync(
        ApplyRefreshPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(request);
        if (target is null)
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                "No remote connection to apply the refresh policy on. Partition generation runs on the server, so only deployed models can apply policies.",
                exitCode: 2,
                hint: "Use 'tx connect -s <workspace> -d <model>' or pass -s/-d explicitly.");

        if (!target.IsRemote)
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_REFRESH_NO_REMOTE_TARGET",
                $"Resolved target '{target.Value}' is not a remote endpoint. Only deployed models can apply refresh policies.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model> to target a deployed model.");

        var provider = _providers.ResolveSingle(target);
        if (provider is null)
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2);

        try
        {
            await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (session is not IModelRefreshSession refresher)
                return TomixResult<RefreshPolicyApplyResult>.Fail(
                    "TOMIX_REFRESH_POLICY_UNSUPPORTED",
                    $"Provider session does not support applying refresh policies: {target.Value}",
                    exitCode: 2,
                    hint: "Apply is only supported on deployed models connected via XMLA (-s <workspace> -d <model>).");

            var result = await refresher.ApplyRefreshPolicyAsync(
                new RefreshPolicyApplyRequest(request.Table, request.EffectiveDate, request.Refresh, request.MaxParallelism),
                cancellationToken).ConfigureAwait(false);

            return TomixResult<RefreshPolicyApplyResult>.Ok(result);
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_AUTH_REQUIRED",
                ex.Message,
                exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (ObjectNotFoundException ex)
        {
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_OBJECT_NOT_FOUND", ex.Message, exitCode: 1, hint: ex.Hint);
        }
        catch (RefreshPolicyNotFoundException ex)
        {
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_REFRESH_POLICY_NOT_FOUND", ex.Message, exitCode: 1,
                hint: "Use 'tx incremental-refresh set' to create a policy first.");
        }
        // Everything else — database resolution failures from OpenAsync (no/multiple databases,
        // a bad --database) and server-side apply rejections — is a real failure, not a missing
        // policy; surface it as apply-failed with the underlying message rather than masking it.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return TomixResult<RefreshPolicyApplyResult>.Fail(
                "TOMIX_REFRESH_POLICY_APPLY_FAILED",
                $"Applying the refresh policy for '{request.Table}' failed: {message}",
                exitCode: 1);
        }
    }

    /// <summary>
    /// Same target resolution as refresh: primary reference if remote, otherwise the
    /// workspace-mode secondary when it is remote, otherwise null.
    /// </summary>
    private ModelReference? ResolveTarget(ApplyRefreshPolicyRequest request)
    {
        var resolver = new ActiveModelResolver(_resolveSession);
        var primary = resolver.ResolveReference(request.Model, request.Database, request.Server);
        if (primary.IsRemote)
            return primary;

        var secondary = resolver.ResolveSyncTarget();
        return secondary is { IsRemote: true } ? secondary : null;
    }
}
