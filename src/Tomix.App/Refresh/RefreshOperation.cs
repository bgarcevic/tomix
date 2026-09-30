using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Refresh;

/// <summary>
/// One refresh against a session already open on its remote target (see
/// <see cref="RefreshModelHandler.OpenAsync"/>). <see cref="PreviewAsync"/> and
/// <see cref="ApplyAsync"/> share that session, so previewing and then applying connects once.
/// </summary>
public sealed class RefreshOperation : IAsyncDisposable
{
    private readonly TomixResult<RefreshModelResult>? _failure;
    private readonly IModelSession? _session;
    private readonly ModelReference? _target;
    private readonly RefreshModelRequest? _request;
    private RefreshPolicyInfo? _policy;

    internal RefreshOperation(IModelSession session, ModelReference target, RefreshModelRequest request)
    {
        _session = session;
        _target = target;
        _request = request;
    }

    private RefreshOperation(TomixResult<RefreshModelResult> failure) => _failure = failure;

    internal static RefreshOperation Failed(TomixResult<RefreshModelResult> failure) => new(failure);

    /// <summary>The validation or connection failure, when the refresh cannot proceed.</summary>
    public TomixResult<RefreshModelResult>? Failure => _failure;

    /// <summary>
    /// The TMSL the refresh would send, or for <c>--policy-only</c> a validated operation summary
    /// (the table and its policy are checked; exact partition changes are decided on execution).
    /// </summary>
    public Task<TomixResult<RefreshModelResult>> PreviewAsync(CancellationToken cancellationToken)
        => Task.FromResult(Preview());

    private TomixResult<RefreshModelResult> Preview()
    {
        if (_failure is not null)
            return _failure;

        var target = _target!;
        var request = _request!;
        try
        {
            if (request.PolicyOnly)
            {
                var policy = GetPolicy();
                return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(target.Value, target.Database,
                    "policyOnly", 0, [], null, null,
                    PolicyPreview: new PolicyOnlyPreview(policy.Table, EffectiveDate, request.MaxParallelism)));
            }

            var script = ((IModelRefreshSession)_session!).GenerateRefreshScript(SessionRequest);
            return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(
                target.Value, target.Database, RefreshModelHandler.NormalizeType(request.RefreshType), 0,
                Array.Empty<RefreshTableResult>(), null, script));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RefreshModelHandler.MapFailure(ex, target, request.PolicyOnly);
        }
    }

    /// <summary>Runs the refresh, or for <c>--policy-only</c> applies the deployed policy without loading data.</summary>
    public async Task<TomixResult<RefreshModelResult>> ApplyAsync(
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        if (_failure is not null)
            return _failure;

        var target = _target!;
        var request = _request!;
        try
        {
            if (request.PolicyOnly)
            {
                var policy = GetPolicy();
                var applied = await ((IRefreshPolicyApplySession)_session!).ApplyRefreshPolicyAsync(
                    new RefreshPolicyApplyRequest(policy.Table, EffectiveDate, Refresh: false, request.MaxParallelism),
                    cancellationToken).ConfigureAwait(false);
                return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(applied.Server, applied.Database,
                    "policyOnly", applied.DurationMs, [], null, null, PolicyApplication: applied));
            }

            var result = await ((IModelRefreshSession)_session!)
                .RefreshAsync(SessionRequest, progress, traceWriter, cancellationToken).ConfigureAwait(false);
            return TomixResult<RefreshModelResult>.Ok(new RefreshModelResult(
                result.Server, result.Database, result.RefreshType, result.DurationMs, result.Tables, result.Totals, Script: null,
                Phases: result.Phases));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RefreshModelHandler.MapFailure(ex, target, request.PolicyOnly);
        }
    }

    private DateOnly EffectiveDate => _request!.EffectiveDate ?? DateOnly.FromDateTime(DateTime.Today);

    private ModelRefreshRequest SessionRequest => new(
        Database: _target!.Database,
        RefreshType: _request!.RefreshType,
        Tables: _request.Tables,
        Partitions: _request.Partitions,
        ApplyRefreshPolicy: _request.ApplyRefreshPolicy,
        EffectiveDate: _request.EffectiveDate,
        MaxParallelism: _request.MaxParallelism);

    /// <summary>The deployed policy for the one --policy-only table, read once per operation.</summary>
    private RefreshPolicyInfo GetPolicy()
    {
        if (_policy is not null)
            return _policy;

        var table = _request!.Tables![0];
        return _policy = ((IRefreshPolicyMutationSession)_session!).GetRefreshPolicy(table)
            ?? throw new RefreshPolicyNotFoundException($"Table '{table}' has no refresh policy. Save a policy on the deployed model first.");
    }

    public ValueTask DisposeAsync() => _session?.DisposeAsync() ?? ValueTask.CompletedTask;
}
