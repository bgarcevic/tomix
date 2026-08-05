using Tomix.Core.Models;

namespace Tomix.App.Tests.Support;

/// <summary>
/// Query-capable session and provider stubs for the handlers that run DAX
/// (<c>QueryModelHandler</c>, <c>TestRunHandler</c>).
/// </summary>
/// <remarks>
/// These were duplicated between QueryModelHandlerTests and TestRunHandlerTests, the latter's own
/// comment reading "Stubs (mirroring QueryModelHandlerTests)" — four of the classes were
/// byte-identical and only the session's canned result differed, which is now the
/// <see cref="Session.Result"/>/<see cref="Session.OnQuery"/> pair.
/// </remarks>
public static class QueryStubs
{
    /// <summary>The result a <see cref="Session"/> returns when nothing overrides it.</summary>
    public static ModelQueryResult DefaultResult { get; } = new(
        "stub-server",
        "stub-db",
        [new QueryColumn("Sales[Amount]", "decimal")],
        [[100.5m], [null]],
        Truncated: true,
        DurationMs: 7);

    /// <summary>A one-column rowset with a row per value, for snapshot-comparison tests.</summary>
    public static ModelQueryResult Rowset(params object?[] values) => new(
        "stub-server",
        "stub-db",
        [new QueryColumn("[Value]", "int64")],
        values.Select(v => (IReadOnlyList<object?>)[v]).ToList(),
        Truncated: false,
        DurationMs: 3);

    /// <summary>Records what it was asked and answers with a canned result.</summary>
    public sealed class Session : IModelSession, IModelQuerySession
    {
        /// <summary>Answers per request. Takes precedence over <see cref="Result"/>.</summary>
        public Func<ModelQueryRequest, ModelQueryResult>? OnQuery { get; init; }

        /// <summary>The fixed answer when <see cref="OnQuery"/> is unset.</summary>
        public ModelQueryResult Result { get; init; } = DefaultResult;

        /// <summary>Thrown from the query instead of answering, for failure-path tests.</summary>
        public Exception? Throw { get; init; }

        public IReadOnlyList<QueryRun>? Runs { get; init; }

        public IReadOnlyList<QueryPlan>? Plans { get; init; }

        public ModelQueryRequest? LastRequest { get; private set; }

        public TextWriter? LastTraceWriter { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelQueryResult> ExecuteQueryAsync(
            ModelQueryRequest request,
            TextWriter? traceWriter,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastTraceWriter = traceWriter;
            if (Throw is not null)
                throw Throw;

            var result = OnQuery is not null ? OnQuery(request) : Result;
            return Task.FromResult(result with { Runs = Runs ?? result.Runs, Plans = Plans ?? result.Plans });
        }
    }

    /// <summary>Claims remote references and hands back <paramref name="session"/>.</summary>
    public sealed class Provider(Session session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromResult<IModelSession>(session);
    }

    /// <summary>Claims remote references but opens a session with no query capability.</summary>
    public sealed class NonQueryProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromResult<IModelSession>(new NonQuerySession());
    }

    public sealed class NonQuerySession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Claims remote references, then fails at open — a connection that cannot be made.</summary>
    public sealed class ThrowingProvider(Exception exception) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw exception;
    }
}
