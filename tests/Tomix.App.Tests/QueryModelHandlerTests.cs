using Tomix.App.Query;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class QueryModelHandlerTests
{
    private static readonly Func<CliConnectionState?> RemoteState =
        () => new CliConnectionState(
            Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            Database: "MyModel",
            Model: null,
            Auth: null,
            Local: false,
            Profile: null,
            Workspace: null);

    private static readonly Func<CliConnectionState?> LocalState =
        () => new CliConnectionState(
            Server: null,
            Database: null,
            Model: "./my-model.tmdl",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: null);

    [Fact]
    public async Task HandleAsync_ReturnsRequired_WhenQueryBlank()
    {
        var handler = new QueryModelHandler([new StubQueryProvider(new StubQuerySession())], RemoteState);
        var result = await handler.HandleAsync(Request(query: "   "), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_QUERY_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("SUMMARIZE(Sales)")]
    [InlineData("// only a comment")]
    [InlineData("/* unterminated block")]
    public async Task HandleAsync_ReturnsInvalid_WhenQueryDoesNotStartWithStatement(string query)
    {
        var handler = new QueryModelHandler([new StubQueryProvider(new StubQuerySession())], RemoteState);
        var result = await handler.HandleAsync(Request(query: query), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_QUERY_INVALID", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("EVALUATE 'Sales'")]
    [InlineData("evaluate ROW(\"x\", 1)")]
    [InlineData("DEFINE MEASURE Sales[X] = 1 EVALUATE ROW(\"x\", [X])")]
    [InlineData("SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES")]
    [InlineData("// comment first\nEVALUATE 'Sales'")]
    [InlineData("-- sql-style comment\nSELECT * FROM $SYSTEM.DISCOVER_SESSIONS")]
    [InlineData("/* block */ EVALUATE 'Sales'")]
    public async Task HandleAsync_AcceptsValidLeadingKeywords(string query)
    {
        var session = new StubQuerySession();
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);
        var result = await handler.HandleAsync(Request(query: query), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(query, session.LastRequest!.Query);
    }

    [Fact]
    public async Task HandleAsync_SkipsValidation_WithNoValidate()
    {
        var session = new StubQuerySession();
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);
        var result = await handler.HandleAsync(
            Request(query: "SUMMARIZE(Sales)", noValidate: true),
            null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("SUMMARIZE(Sales)", session.LastRequest!.Query);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNoRemoteTarget_WhenConnectionIsLocal()
    {
        var handler = new QueryModelHandler([new StubQueryProvider(new StubQuerySession())], LocalState);
        var result = await handler.HandleAsync(Request(query: "EVALUATE 'Sales'"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_QUERY_NO_REMOTE_TARGET", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsUnsupported_WhenSessionIsNotQueryCapable()
    {
        var handler = new QueryModelHandler([new StubNonQueryProvider()], RemoteState);
        var result = await handler.HandleAsync(Request(query: "EVALUATE 'Sales'"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_QUERY_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAuthRequired_WhenProviderThrowsAuthException()
    {
        var handler = new QueryModelHandler(
            [new ThrowingProvider(new AuthenticationRequiredException("login"))], RemoteState);
        var result = await handler.HandleAsync(Request(query: "EVALUATE 'Sales'"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsQueryFailed_WhenExecutionThrows()
    {
        var session = new StubQuerySession { Throw = new InvalidOperationException("syntax error near BAD") };
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);
        var result = await handler.HandleAsync(Request(query: "EVALUATE BAD("), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_QUERY_FAILED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("syntax error near BAD", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task HandleAsync_MapsRowsetAndForwardsLimitAndParameters()
    {
        var session = new StubQuerySession();
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);
        var result = await handler.HandleAsync(
            Request(
                query: "EVALUATE 'Sales'",
                parameters: new Dictionary<string, string> { ["color"] = "Red" },
                limit: 10),
            null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(10, session.LastRequest!.MaxRows);
        Assert.Equal("Red", session.LastRequest.Parameters!["color"]);

        var data = result.Data!;
        Assert.Equal("stub-server", data.Server);
        Assert.Equal("stub-db", data.Database);
        Assert.Equal(["Sales[Amount]"], data.Columns.Select(c => c.Name));
        Assert.Equal(2, data.RowCount);
        Assert.True(data.Truncated);
        Assert.Equal(7, data.DurationMs);
    }

    [Fact]
    public async Task HandleAsync_ForwardsPerfOptionsAndTraceWriter()
    {
        var session = new StubQuerySession();
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);
        var writer = new StringWriter();

        var result = await handler.HandleAsync(
            Request(query: "EVALUATE 'Sales'", trace: true, plan: true, cold: true, runs: 3),
            writer,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(session.LastRequest!.Trace);
        Assert.True(session.LastRequest.Plan);
        Assert.True(session.LastRequest.ClearCache);
        Assert.Equal(3, session.LastRequest.Runs);
        Assert.Same(writer, session.LastTraceWriter);
    }

    [Fact]
    public async Task HandleAsync_ClampsRunsBelowOneToOne()
    {
        var session = new StubQuerySession();
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);

        await handler.HandleAsync(Request(query: "EVALUATE 'Sales'", runs: 0), null, CancellationToken.None);

        Assert.Equal(1, session.LastRequest!.Runs);
    }

    [Fact]
    public async Task HandleAsync_SurfacesTimingsPlansAndBenchmark_FromRuns()
    {
        var runs = new List<QueryRun>
        {
            new(1, Cold: true, ClientMs: 100, Timings: new QueryTimings(90, 120, 30, 60, 80, 2, 1)),
            new(2, Cold: true, ClientMs: 80, Timings: new QueryTimings(70, 100, 20, 50, 70, 2, 0))
        };
        var plans = new List<QueryPlan> { new("logical", "AddColumns: ..."), new("physical", "Spool: ...") };
        var session = new StubQuerySession { Runs = runs, Plans = plans };
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);

        var result = await handler.HandleAsync(
            Request(query: "EVALUATE 'Sales'", trace: true, plan: true, runs: 2),
            null,
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.Equal(90, data.Timings!.TotalMs);          // first run's server timings
        Assert.Equal(plans, data.Plans);
        Assert.NotNull(data.Benchmark);
        Assert.Equal(2, data.Benchmark!.Runs.Count);
        Assert.Equal(80, data.Benchmark.TotalStats.Avg);  // (90 + 70) / 2
        Assert.Equal(70, data.Benchmark.TotalStats.Min);
        Assert.Equal(90, data.Benchmark.TotalStats.Max);
    }

    [Fact]
    public async Task HandleAsync_DegradesGracefully_WhenNoTimingsCaptured()
    {
        // Trace requested but the provider returned no runs (e.g. non-admin, tracing unavailable).
        var session = new StubQuerySession { Runs = null };
        var handler = new QueryModelHandler([new StubQueryProvider(session)], RemoteState);

        var result = await handler.HandleAsync(
            Request(query: "EVALUATE 'Sales'", trace: true),
            null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.Timings);
        Assert.Null(result.Data.Benchmark);
        Assert.Equal(2, result.Data.RowCount);  // rowset still returned
    }

    [Theory]
    [InlineData("EVALUATE 'Sales'", "EVALUATE")]
    [InlineData("  \t\n evaluate x", "evaluate")]
    [InlineData("// c1\n-- c2\n/* c3 */ SELECT 1", "SELECT")]
    [InlineData("", "")]
    [InlineData("/* never closed", "")]
    [InlineData("123", "")]
    public void FirstSignificantToken_SkipsCommentsAndWhitespace(string query, string expected)
        => Assert.Equal(expected, QueryModelHandler.FirstSignificantToken(query));

    private static QueryModelRequest Request(
        string? query = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        int? limit = null,
        bool noValidate = false,
        bool trace = false,
        bool plan = false,
        bool cold = false,
        int runs = 1) =>
        new(Model: null,
            Server: null,
            Database: null,
            Auth: null,
            Query: query,
            Parameters: parameters,
            Limit: limit,
            NoValidate: noValidate,
            Trace: trace,
            TracePath: null,
            Plan: plan,
            Cold: cold,
            Runs: runs);

    private sealed class StubQueryProvider : IModelProvider
    {
        private readonly StubQuerySession _session;
        public StubQueryProvider(StubQuerySession session) => _session = session;
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class StubQuerySession : IModelSession, IModelQuerySession
    {
        public ModelQueryRequest? LastRequest { get; private set; }
        public TextWriter? LastTraceWriter { get; private set; }
        public Exception? Throw { get; init; }
        public IReadOnlyList<QueryRun>? Runs { get; init; }
        public IReadOnlyList<QueryPlan>? Plans { get; init; }
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
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
            return Task.FromResult(new ModelQueryResult(
                "stub-server",
                "stub-db",
                [new QueryColumn("Sales[Amount]", "decimal")],
                [[100.5m], [(object?)null]],
                Truncated: true,
                DurationMs: 7,
                Runs: Runs,
                Plans: Plans));
        }
    }

    private sealed class StubNonQueryProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubNonQuerySession());
    }

    private sealed class StubNonQuerySession : IModelSession
    {
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        private readonly Exception _exception;
        public ThrowingProvider(Exception exception) => _exception = exception;
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => throw _exception;
    }
}
