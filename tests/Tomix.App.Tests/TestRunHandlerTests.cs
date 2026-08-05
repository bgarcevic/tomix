using Tomix.App.State;
using Tomix.App.Test;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class TestRunHandlerTests : IDisposable
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

    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-testrun-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteDax(string name, string content = "EVALUATE 'Sales'")
    {
        var path = Path.Combine(_dir, name + ".dax");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ModelQueryResult Rowset(params object?[] values) => QueryStubs.Rowset(values);

    /// <summary>A session answering with a single-row rowset unless the test overrides it.</summary>
    private static QueryStubs.Session NewSession(
        Func<ModelQueryRequest, ModelQueryResult>? onQuery = null)
        => new() { OnQuery = onQuery ?? (_ => Rowset(1L)) };

    private static TestRunHandler Handler(QueryStubs.Session session, Func<CliConnectionState?>? state = null)
        => new([new QueryStubs.Provider(session)], state ?? RemoteState);

    private TestRunRequest Request(bool update = false, string? filter = null,
        IReadOnlyDictionary<string, string>? parameters = null, int maxRows = 10000, string? path = null)
        => new(Model: null, Server: null, Database: null, Auth: null,
            Path: path ?? _dir, Update: update, Filter: filter, Parameters: parameters, MaxRows: maxRows);

    // ── Pre-condition failures ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ReturnsPathNotFound_WhenPathMissing()
    {
        var result = await Handler(NewSession()).HandleAsync(
            Request(path: Path.Combine(_dir, "nope")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_PATH_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNoneFound_WhenDirectoryHasNoDaxFiles()
    {
        var result = await Handler(NewSession()).HandleAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_NONE_FOUND", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNoneFound_WhenFilterMatchesNothing()
    {
        WriteDax("sales");
        var result = await Handler(NewSession()).HandleAsync(
            Request(filter: "other-*"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_NONE_FOUND", result.Diagnostics[0].Code);
        Assert.Contains("other-*", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNoRemoteTarget_WhenConnectionIsLocal()
    {
        WriteDax("sales");
        var result = await Handler(NewSession(), LocalState).HandleAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_NO_REMOTE_TARGET", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsUnsupported_WhenSessionIsNotQueryCapable()
    {
        WriteDax("sales");
        var handler = new TestRunHandler([new QueryStubs.NonQueryProvider()], RemoteState);
        var result = await handler.HandleAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAuthRequired_WhenProviderThrowsAuthException()
    {
        WriteDax("sales");
        var handler = new TestRunHandler(
            [new QueryStubs.ThrowingProvider(new AuthenticationRequiredException("login"))], RemoteState);
        var result = await handler.HandleAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    // ── Update mode ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Update_WritesSnapshotAndExitsZero()
    {
        var daxPath = WriteDax("sales");
        var result = await Handler(NewSession()).HandleAsync(Request(update: true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Updated, test.Outcome);
        Assert.Equal(1, result.Data.Updated);

        var snapshot = TestSnapshotFile.Load(Path.ChangeExtension(daxPath, ".expected.json"), out var error);
        Assert.Null(error);
        Assert.Equal(TestSnapshotFile.ComputeQueryHash("EVALUATE 'Sales'"), snapshot!.QuerySha256);
        Assert.Equal("1", snapshot.Rows[0][0]);
    }

    [Fact]
    public async Task HandleAsync_Update_ReportsUnchanged_WhenResultIsIdentical()
    {
        WriteDax("sales");
        var handler = Handler(NewSession());

        await handler.HandleAsync(Request(update: true), CancellationToken.None);
        var second = await handler.HandleAsync(Request(update: true), CancellationToken.None);

        Assert.Equal(TestOutcome.Unchanged, Assert.Single(second.Data!.Tests).Outcome);
        Assert.Equal(0, second.ExitCode);
    }

    // ── Compare mode ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_PassesAndExitsZero_WhenResultMatchesSnapshot()
    {
        WriteDax("sales");
        var handler = Handler(NewSession());
        await handler.HandleAsync(Request(update: true), CancellationToken.None);

        var result = await handler.HandleAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
        Assert.Equal(1, result.Data.Passed);
        Assert.Equal("stub-server", result.Data.Server);
        Assert.Equal("stub-db", result.Data.Database);
    }

    [Fact]
    public async Task HandleAsync_FailsWithDifferences_WhenResultDrifts()
    {
        WriteDax("sales");
        var handler = Handler(NewSession());
        await handler.HandleAsync(Request(update: true), CancellationToken.None);

        var drifted = Handler(NewSession(_ => Rowset(2L)));
        var result = await drifted.HandleAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);                    // report renders; exit code carries the failure
        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal(1, result.Data.Failed);
        var difference = Assert.Single(test.Differences!);
        Assert.Equal("cell", difference.Kind);
        Assert.Equal("1", difference.Expected);
        Assert.Equal("2", difference.Actual);
    }

    [Fact]
    public async Task HandleAsync_ReportsMissing_WhenSnapshotAbsent()
    {
        WriteDax("sales");
        var result = await Handler(NewSession()).HandleAsync(Request(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Missing, test.Outcome);
        Assert.Contains("--update", test.Message);
        Assert.Equal(1, result.Data.Missing);
    }

    [Fact]
    public async Task HandleAsync_MentionsQueryChange_WhenFailingWithStaleHash()
    {
        var daxPath = WriteDax("sales");
        var handler = Handler(NewSession());
        await handler.HandleAsync(Request(update: true), CancellationToken.None);

        // Query text changes (new hash) and its result drifts from the stale snapshot.
        File.WriteAllText(daxPath, "EVALUATE ROW(\"Value\", 2)");
        var drifted = Handler(NewSession(_ => Rowset(2L)));
        var result = await drifted.HandleAsync(Request(), CancellationToken.None);

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Contains("query changed", test.Message);
    }

    // ── Per-test errors ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ReportsError_WhenResultTruncated()
    {
        WriteDax("sales");
        var session = NewSession(_ => Rowset(1L) with { Truncated = true });
        var result = await Handler(session).HandleAsync(Request(maxRows: 5), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Error, test.Outcome);
        Assert.Contains("--max-rows (5)", test.Message);
    }

    [Fact]
    public async Task HandleAsync_ReportsError_WhenSnapshotRowsDoNotMatchColumns()
    {
        var daxPath = WriteDax("sales");
        // Structurally incomplete snapshot: one column declared, but a row with no cells.
        File.WriteAllText(Path.ChangeExtension(daxPath, ".expected.json"), """
            { "version": 1, "querySha256": "abc",
              "columns": [{ "name": "[Value]", "type": "int64" }],
              "rows": [[]] }
            """);

        var result = await Handler(NewSession()).HandleAsync(Request(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Error, test.Outcome);
        Assert.Contains("row 1", test.Message);
    }

    [Fact]
    public async Task HandleAsync_ReportsError_ForNonQueryStatement()
    {
        WriteDax("bad", "SUMMARIZE(Sales)");
        var result = await Handler(NewSession()).HandleAsync(Request(), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Error, test.Outcome);
        Assert.Contains("SUMMARIZE", test.Message);
    }

    [Fact]
    public async Task HandleAsync_ContinuesAfterPerTestQueryFailure()
    {
        WriteDax("a-boom", "EVALUATE BOOM()");
        WriteDax("b-good");
        var session = NewSession(request => request.Query.Contains("BOOM")
            ? throw new InvalidOperationException("syntax error near BOOM")
            : Rowset(1L));
        var result = await Handler(session).HandleAsync(Request(update: true), CancellationToken.None);

        Assert.Equal(2, result.Data!.Tests.Count);
        Assert.Equal(TestOutcome.Error, result.Data.Tests[0].Outcome);
        Assert.Contains("syntax error near BOOM", result.Data.Tests[0].Message);
        Assert.Equal(TestOutcome.Updated, result.Data.Tests[1].Outcome);
        Assert.Equal(1, result.ExitCode);
    }

    // ── Request forwarding ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ForwardsParametersAndMaxRows()
    {
        WriteDax("sales");
        var session = NewSession();
        await Handler(session).HandleAsync(
            Request(update: true, parameters: new Dictionary<string, string> { ["color"] = "Red" }, maxRows: 42),
            CancellationToken.None);

        Assert.Equal("Red", session.LastRequest!.Parameters!["color"]);
        Assert.Equal(42, session.LastRequest.MaxRows);
    }

    [Fact]
    public async Task HandleAsync_Filter_RunsOnlyMatchingTests()
    {
        WriteDax("totals/sales");
        WriteDax("other/costs");
        var result = await Handler(NewSession()).HandleAsync(
            Request(update: true, filter: "totals/*"), CancellationToken.None);

        Assert.Equal("totals/sales", Assert.Single(result.Data!.Tests).Name);
    }
}
