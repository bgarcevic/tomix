using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// <see cref="ILiveSessionLease.Session"/>: the live model as an ordinary session, for exactly as
/// long as its lease lasts (ADR 0002 §1). It offers the capabilities of the one-shot session for
/// the same source, writes through the session journal's <see cref="TomWriter"/>, and takes the
/// <see cref="Database"/> from the journal on every call, since a rollback can replace it.
/// </summary>
internal class TomLeaseView : IModelSession, IModelExportSession, IModelMutationSession,
    IExpressionRewriteSession, IObjectMoveSession, IRefreshPolicyMutationSession, IModelDeploySession
{
    private readonly TomLiveModelSession _session;
    private readonly TomLiveModelSession.Lease _lease;

    public TomLeaseView(TomLiveModelSession session, TomLiveModelSession.Lease lease)
    {
        _session = session;
        _lease = lease;
    }

    public string SourcePath => _session.SourcePath;

    protected TomModelSource Source => _session.Source;

    /// <summary>The model, if the lease is still open.</summary>
    protected Database Database
    {
        get
        {
            _lease.ThrowIfEnded();
            return _session.Journal.Database;
        }
    }

    private TomWriter Writer => _session.Journal.Writer;

    private TomModelMutator Mutator() => new(Database, Writer);

    public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Source.Summarize(Database));
    }

    public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lease.ThrowIfEnded();
        return Task.FromResult(_session.Snapshot());
    }

    /// <summary>Does nothing: the lease ends the view, and the host closes the session.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<ModelExportResult> ExportAsync(ModelExportRequest request, CancellationToken cancellationToken)
        => Source.ExportAsync(Database, request, cancellationToken);

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => Mutator().AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => Mutator().SetProperty(request);

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => Mutator().RemoveObject(request);

    public ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request) => Mutator().MoveObject(request);

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => Mutator().ReplaceText(request);

    public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        => Mutator().RewriteExpressions(edits);

    public RefreshPolicyInfo? GetRefreshPolicy(string table)
        => new TomRefreshPolicyManager(Database, Writer).Get(table);

    public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request)
        => new TomRefreshPolicyManager(Database, Writer).Set(request);

    public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false)
        => new TomRefreshPolicyManager(Database, Writer).Remove(table, ifExists);

    public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
    {
        _lease.ThrowIfEnded();
        return _session.SaveAsync(_lease, outputPath, serialization, overwrite, cancellationToken);
    }

    public Task<ModelDeployResult> DeployAsync(ModelDeployRequest request, CancellationToken cancellationToken)
        => TomModelDeployer.DeployAsync(Database, request, Source.TokenProvider, cancellationToken);

    public Task<string> GenerateScriptAsync(ModelDeployRequest request, CancellationToken cancellationToken)
        => TomModelDeployer.GenerateScriptAsync(Database, request, Source.TokenProvider, cancellationToken);

    public Task<ModelDeployPlan> GeneratePlanAsync(ModelDeployRequest request, CancellationToken cancellationToken)
        => TomModelDeployer.GeneratePlanAsync(Database, request, Source.TokenProvider, cancellationToken);
}

/// <summary>The lease view of a server-backed session, which can also query and refresh.</summary>
internal sealed class TomServerLeaseView(TomLiveModelSession session, TomLiveModelSession.Lease lease, TomServerModelSource server)
    : TomLeaseView(session, lease), IModelRefreshSession, IRefreshPolicyApplySession, IModelQuerySession
{
    public Task<ModelRefreshResult> RefreshAsync(
        ModelRefreshRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
        => TomModelRefresher.RefreshAsync(server.Server, Database, request, progress, traceWriter, cancellationToken);

    public string GenerateRefreshScript(ModelRefreshRequest request)
        => TomModelRefresher.GenerateRefreshScript(Database, request);

    public Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(RefreshPolicyApplyRequest request, CancellationToken cancellationToken)
        => TomRefreshPolicyApplier.ApplyAsync(server.Server, Database, request, cancellationToken);

    public Task<ModelQueryResult> ExecuteQueryAsync(ModelQueryRequest request, TextWriter? traceWriter, CancellationToken cancellationToken)
    {
        var database = Database;
        return TomServerModelSession.ExecuteQueryAsync(
            server.Reference, database, server.ModelName(database), server.TokenProvider, request, traceWriter, cancellationToken);
    }
}
