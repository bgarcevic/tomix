using System.Diagnostics;
using Tomix.App.Query;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Test;

/// <summary>
/// Runs DAX regression tests: discovers <c>.dax</c> files, executes each against the resolved
/// live model (same target resolution as <see cref="QueryModelHandler"/>), and either compares
/// the rowset to the paired <c>.expected.json</c> snapshot or re-records it (<c>--update</c>).
/// Per-test problems are result data (like BPA violations) so the run continues and the full
/// report renders; only authentication failures abort. Any non-passing test yields exit code 1
/// via the Ok-with-exit-code pattern from <see cref="Bpa.BpaRunHandler"/>.
/// </summary>
public sealed class TestRunHandler
{
    private static readonly string[] ValidLeadingKeywords = ["DEFINE", "EVALUATE", "SELECT"];

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;

    public TestRunHandler(IEnumerable<IModelProvider> providers, Func<CliConnectionState?> resolveSession)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
    }

    public async Task<TomixResult<TestRunResult>> HandleAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_PATH_NOT_FOUND",
                $"Test path not found: {request.Path}",
                exitCode: 2,
                hint: "Pass a .dax file or a directory containing .dax test files.");

        var tests = TestDiscovery.Discover(request.Path)
            .Where(test => TestDiscovery.MatchesFilter(test.Name, request.Filter))
            .ToList();
        if (tests.Count == 0)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_NONE_FOUND",
                string.IsNullOrWhiteSpace(request.Filter)
                    ? $"No .dax test files found under: {request.Path}"
                    : $"No .dax test files match --filter {request.Filter} under: {request.Path}",
                exitCode: 2,
                hint: "Each test is a .dax file with a sibling .expected.json snapshot (record with 'tx test --update').");

        var target = QueryModelHandler.ResolveTarget(
            request.Model, request.Database, request.Server, new ActiveModelResolver(_resolveSession));
        if (target is null || !target.IsRemote)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_NO_REMOTE_TARGET",
                "No live model to test against. Test queries execute on a deployed model or a local instance, not on TMDL/BIM files.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model>, connect to a local instance with -s localhost:<port>, or deploy the local model first ('tx deploy').");

        var provider = _providers.ResolveSingle(target);
        if (provider is null)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2);

        try
        {
            await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (session is not IModelQuerySession querySession)
                return TomixResult<TestRunResult>.Fail(
                    "TOMIX_TEST_UNSUPPORTED",
                    $"Provider session does not support queries: {target.Value}",
                    exitCode: 2,
                    hint: "Tests run only on live models connected via XMLA (-s <workspace> -d <model>).");

            return await RunTestsAsync(request, target, querySession, tests, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_AUTH_REQUIRED",
                ex.Message,
                exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
    }

    private static async Task<TomixResult<TestRunResult>> RunTestsAsync(
        TestRunRequest request,
        ModelReference target,
        IModelQuerySession querySession,
        IReadOnlyList<DiscoveredTest> tests,
        CancellationToken cancellationToken)
    {
        var maxRows = Math.Max(1, request.MaxRows);
        var runWatch = Stopwatch.StartNew();
        var results = new List<TestCaseResult>(tests.Count);
        string? server = null;
        string? database = null;

        foreach (var test in tests)
        {
            var watch = Stopwatch.StartNew();

            string query;
            try
            {
                query = await File.ReadAllTextAsync(test.DaxPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                results.Add(Case(test, TestOutcome.Error, watch, $"Query file could not be read: {ex.Message}"));
                continue;
            }

            var keyword = QueryModelHandler.FirstSignificantToken(query);
            if (!ValidLeadingKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(Case(test, TestOutcome.Error, watch, keyword.Length == 0
                    ? "The query file contains no statement."
                    : $"The query starts with '{keyword}', which is not a query statement (expected EVALUATE, DEFINE, or SELECT)."));
                continue;
            }

            ModelQueryResult actual;
            try
            {
                actual = await querySession.ExecuteQueryAsync(
                    new ModelQueryRequest(query, request.Parameters, maxRows),
                    traceWriter: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AuthenticationRequiredException)
            {
                var message = ex is InvalidOperationException ? ex.Message : ex.InnerException?.Message ?? ex.Message;
                results.Add(Case(test, TestOutcome.Error, watch, $"Query failed: {message}"));
                continue;
            }

            server ??= actual.Server;
            database ??= actual.Database;

            if (actual.Truncated)
            {
                results.Add(Case(test, TestOutcome.Error, watch,
                    $"Result exceeded --max-rows ({maxRows}). Regression queries should return small, stable rowsets."));
                continue;
            }

            var queryHash = TestSnapshotFile.ComputeQueryHash(query);
            if (request.Update)
            {
                results.Add(UpdateSnapshot(test, actual, queryHash, watch, out var writeError));
                if (writeError is not null)
                    return TomixResult<TestRunResult>.Fail(
                        "TOMIX_TEST_UPDATE_FAILED",
                        $"Could not write snapshot for '{test.Name}': {writeError}",
                        exitCode: 1);
            }
            else
            {
                results.Add(CompareSnapshot(test, actual, queryHash, watch));
            }
        }

        var runResult = new TestRunResult(
            Server: server ?? target.Value,
            Database: database ?? target.Database ?? "",
            Path: request.Path,
            Tests: results,
            Passed: results.Count(r => r.Outcome == TestOutcome.Passed),
            Failed: results.Count(r => r.Outcome == TestOutcome.Failed),
            Missing: results.Count(r => r.Outcome == TestOutcome.Missing),
            Errored: results.Count(r => r.Outcome == TestOutcome.Error),
            Updated: results.Count(r => r.Outcome == TestOutcome.Updated),
            DurationMs: runWatch.ElapsedMilliseconds);

        var anyNotPassed = results.Any(r => r.Outcome is TestOutcome.Failed or TestOutcome.Missing or TestOutcome.Error);
        return TomixResult<TestRunResult>.Ok(runResult, exitCode: anyNotPassed ? 1 : 0);
    }

    private static TestCaseResult CompareSnapshot(
        DiscoveredTest test,
        ModelQueryResult actual,
        string queryHash,
        Stopwatch watch)
    {
        if (!File.Exists(test.ExpectedPath))
            return Case(test, TestOutcome.Missing, watch,
                "No snapshot recorded. Run 'tx test --update' to record it.");

        var snapshot = TestSnapshotFile.Load(test.ExpectedPath, out var loadError);
        if (snapshot is null)
            return Case(test, TestOutcome.Error, watch, loadError);

        var comparison = TestResultComparer.Compare(snapshot, actual);
        if (comparison.Passed)
            return Case(test, TestOutcome.Passed, watch);

        var message = $"{comparison.TotalDifferences} difference(s).";
        if (!string.Equals(snapshot.QuerySha256, queryHash, StringComparison.Ordinal))
            message += " The query changed since the snapshot was recorded — run 'tx test --update' if intended.";

        return Case(test, TestOutcome.Failed, watch, message,
            comparison.Differences, comparison.TotalDifferences);
    }

    private static TestCaseResult UpdateSnapshot(
        DiscoveredTest test,
        ModelQueryResult actual,
        string queryHash,
        Stopwatch watch,
        out string? writeError)
    {
        writeError = null;
        try
        {
            return TestSnapshotFile.Save(test.ExpectedPath, TestSnapshotFile.FromResult(actual, queryHash))
                ? Case(test, TestOutcome.Updated, watch)
                : Case(test, TestOutcome.Unchanged, watch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writeError = ex.Message;
            return Case(test, TestOutcome.Error, watch, $"Snapshot could not be written: {ex.Message}");
        }
    }

    private static TestCaseResult Case(
        DiscoveredTest test,
        TestOutcome outcome,
        Stopwatch watch,
        string? message = null,
        IReadOnlyList<TestDifference>? differences = null,
        int totalDifferences = 0)
        => new(test.Name, test.DaxPath, outcome, watch.ElapsedMilliseconds, message, differences, totalDifferences);
}
