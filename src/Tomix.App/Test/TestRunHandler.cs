using System.Diagnostics;
using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.App.Query;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Test;

/// <summary>
/// Runs DAX regression tests: discovers <c>.dax</c> files, executes each against the resolved
/// live model (same target resolution as <see cref="QueryModelHandler"/>), and either compares
/// the rowset to the paired <c>.expected.json</c> snapshot or re-records it (<c>--update</c>, or
/// <c>--update-missing</c> for tests without one). A re-recorded snapshot reports what it changed,
/// and snapshots whose test is gone are reported as orphans. With a baseline model
/// (<c>--baseline</c>) each query runs on both models at once and the baseline's result takes
/// the snapshot's place: no snapshot is read or written and no orphans are reported.
/// A test's <c>tx:</c> assertions (<see cref="TestAssertions"/>) are checked on the tested model
/// in every mode; one with a result assertion needs no snapshot unless it states <c>snapshot</c>,
/// and one that expects an error is never compared or recorded.
/// Per-test problems are result data (like BPA violations) so the run continues and the full
/// report renders; only authentication failures abort. Any non-passing test yields exit code 1
/// via the Ok-with-exit-code pattern from <see cref="Bpa.BpaRunHandler"/>.
/// </summary>
public sealed class TestRunHandler
{
    private static readonly string[] ValidLeadingKeywords = ["DEFINE", "EVALUATE", "SELECT"];

    private const string QueryFailedPrefix = "Query failed: ";

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;

    public TestRunHandler(IEnumerable<IModelProvider> providers, Func<CliConnectionState?> resolveSession)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
    }

    public async Task<TomixResult<TestRunResult>> HandleAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        if (request.Update && request.UpdateMissing)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_CONFLICTING_OPTIONS",
                "--update and --update-missing cannot be combined; choose one.",
                exitCode: 2,
                hint: "--update-missing records only tests without a snapshot; --update re-records every snapshot.");

        if (request.HasBaseline && (request.Update || request.UpdateMissing))
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_CONFLICTING_OPTIONS",
                "--update and --update-missing cannot be combined with a baseline model; a baseline run reads and writes no snapshots.",
                exitCode: 2,
                hint: "Drop --baseline to record snapshots, or drop --update/--update-missing to compare with the baseline.");

        if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_PATH_NOT_FOUND",
                $"Test path not found: {request.Path}",
                exitCode: 2,
                hint: "Pass a .dax file or a directory containing .dax test files.");

        var tests = TestTags.Select(
            TestDiscovery.Discover(request.Path)
                .Where(test => TestDiscovery.MatchesFilter(test.Name, request.Filter))
                .ToList(),
            request.Tags);
        if (tests.Count == 0)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_NONE_FOUND",
                $"No .dax test files {TestDiscovery.Selection(request.Filter, request.Tags)}under: {request.Path}",
                exitCode: 2,
                hint: request.HasBaseline
                    ? "Each test is a .dax file; with a baseline model it needs no .expected.json snapshot."
                    : "Each test is a .dax file with a sibling .expected.json snapshot (record with 'tx test --update-missing').");

        // A baseline run ignores snapshots, so a snapshot without a test is no concern of it; and
        // an orphan has no .dax to carry tags, so a tagged run cannot tell whether it is in scope.
        var orphans = request.HasBaseline || request.Tags is { Count: > 0 }
            ? []
            : TestDiscovery.FindOrphans(request.Path)
                .Where(orphan => TestDiscovery.MatchesFilter(orphan.Name, request.Filter))
                .ToList();

        var target = QueryModelHandler.ResolveTarget(
            request.Model, request.Database, request.Server, new ActiveModelResolver(_resolveSession));
        if (target is null || !target.IsRemote)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_TEST_NO_REMOTE_TARGET",
                "No live model to test against. Test queries execute on a deployed model or a local instance, not on TMDL/BIM files.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model>, connect to a local instance with -s localhost:<port>, or deploy the local model first ('tx deploy').");

        var provider = _providers.ResolveSingleProvider(target);
        if (provider is null)
            return TomixResult<TestRunResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2);

        ModelReference? baselineTarget = null;
        IModelProvider? baselineProvider = null;
        if (request.HasBaseline)
        {
            baselineTarget = ResolveBaselineTarget(request, target);
            baselineProvider = _providers.ResolveSingleProvider(baselineTarget);
            if (!baselineTarget.IsRemote || baselineProvider is null)
                return TomixResult<TestRunResult>.Fail(
                    "TOMIX_NO_PROVIDER",
                    $"No provider can open the baseline endpoint: {baselineTarget.Value}",
                    exitCode: 2,
                    hint: "Name the baseline with --baseline <profile> or --baseline-server <workspace> --baseline-database <model>.");
        }

        try
        {
            await using var connection = await QueryConnection.OpenAsync(provider, target, cancellationToken).ConfigureAwait(false);
            if (connection is null)
                return Unsupported(target, side: "");

            if (baselineTarget is null)
                return await RunTestsAsync(request, target, connection.Queries, baseline: null, tests, orphans, cancellationToken).ConfigureAwait(false);

            QueryConnection? baselineConnection;
            try
            {
                baselineConnection = await QueryConnection.OpenAsync(baselineProvider!, baselineTarget, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelConnectionException ex)
            {
                return OnBaseline(ProviderConnectionGuard.ConnectionFailure<TestRunResult>(baselineTarget, ex));
            }

            await using (baselineConnection)
            {
                if (baselineConnection is null)
                    return Unsupported(baselineTarget, side: "of the baseline model ");

                return await RunTestsAsync(
                    request, target, connection.Queries, (baselineConnection.Queries, baselineTarget), tests, orphans, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ModelConnectionException ex)
        {
            return ProviderConnectionGuard.ConnectionFailure<TestRunResult>(target, ex);
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

    // The baseline defaults to the tested model's server and database, so --baseline-database
    // alone compares two models in one workspace.
    private static ModelReference ResolveBaselineTarget(TestRunRequest request, ModelReference target)
        => new(
            string.IsNullOrWhiteSpace(request.BaselineServer)
                ? target.Value
                : ModelReference.NormalizeEndpoint(request.BaselineServer),
            string.IsNullOrWhiteSpace(request.BaselineDatabase) ? target.Database : request.BaselineDatabase);

    private static TomixResult<TestRunResult> Unsupported(ModelReference target, string side)
        => TomixResult<TestRunResult>.Fail(
            "TOMIX_TEST_UNSUPPORTED",
            $"Provider session {side}does not support queries: {target.Value}",
            exitCode: 2,
            hint: "Tests run only on live models connected via XMLA (-s <workspace> -d <model>).");

    // Names the baseline in a connection failure, which otherwise reads like one on the tested model.
    private static TomixResult<TestRunResult> OnBaseline(TomixResult<TestRunResult> failure)
    {
        var diagnostic = failure.Diagnostics[0];
        return TomixResult<TestRunResult>.Fail(
            diagnostic.Code, $"Baseline model: {diagnostic.Message}", failure.ExitCode, diagnostic.Hint);
    }

    private static async Task<TomixResult<TestRunResult>> RunTestsAsync(
        TestRunRequest request,
        ModelReference target,
        IModelQuerySession querySession,
        (IModelQuerySession Queries, ModelReference Target)? baseline,
        IReadOnlyList<DiscoveredTest> tests,
        IReadOnlyList<OrphanedSnapshot> orphans,
        CancellationToken cancellationToken)
    {
        var maxRows = Math.Max(1, request.MaxRows);
        var runWatch = Stopwatch.StartNew();
        var results = new List<TestCaseResult>(tests.Count);
        string? server = null;
        string? database = null;
        string? baselineServer = null;
        string? baselineDatabase = null;

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

            var compare = (request.Compare ?? TestCompareOptions.Exact).WithDirectives(query, out var assertions, out var directiveError);
            if (compare is null)
            {
                results.Add(Case(test, TestOutcome.Error, watch, directiveError));
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

            var queryRequest = new ModelQueryRequest(query, request.Parameters, maxRows);
            var queryHash = TestSnapshotFile.ComputeQueryHash(query);

            // A test that expects an error checks only the tested model: it has no result to
            // compare with a snapshot or a baseline model.
            if (assertions.ExpectError)
            {
                var (failedResult, failure) = await ExecuteAsync(querySession, queryRequest, cancellationToken).ConfigureAwait(false);
                server ??= failedResult?.Server;
                database ??= failedResult?.Database;
                results.Add(CheckExpectedError(test, failedResult, failure, assertions, watch));
                continue;
            }

            if (baseline is { } other)
            {
                // Both sides at once; the loop still finishes one test before the next, so the
                // report order is the discovery order.
                var actualRun = ExecuteAsync(querySession, queryRequest, cancellationToken);
                var baselineRun = ExecuteAsync(other.Queries, queryRequest, cancellationToken);
                await Task.WhenAll(actualRun, baselineRun).ConfigureAwait(false);
                var (actualResult, actualError) = actualRun.Result;
                var (baselineResult, baselineError) = baselineRun.Result;

                server ??= actualResult?.Server;
                database ??= actualResult?.Database;
                baselineServer ??= baselineResult?.Server;
                baselineDatabase ??= baselineResult?.Database;

                results.Add(CompareBaseline(test, actualResult, actualError, baselineResult, baselineError, queryHash, compare, assertions, watch));
                continue;
            }

            var (result, error) = await ExecuteAsync(querySession, queryRequest, cancellationToken).ConfigureAwait(false);
            server ??= result?.Server;
            database ??= result?.Database;
            if (error is not null)
            {
                results.Add(Case(test, TestOutcome.Error, watch, error));
                continue;
            }

            var actual = result!;
            var assertionFailures = assertions.Check(actual);

            // A test whose assertions stand in for a snapshot never gets one from recording; --update
            // still re-records one it has. Nothing is recorded while an assertion fails (fix that in
            // the .dax).
            var hasSnapshot = File.Exists(test.ExpectedPath);
            var record = assertionFailures.Count == 0 && (assertions.UsesSnapshot
                ? request.Update || (request.UpdateMissing && !hasSnapshot)
                : request.Update && hasSnapshot);
            if (record)
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
                results.Add(CompareSnapshot(test, actual, queryHash, compare, assertions, assertionFailures, watch));
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
            DurationMs: runWatch.ElapsedMilliseconds,
            Orphans: orphans,
            Baseline: baseline is { } compared
                ? new TestBaselineTarget(
                    baselineServer ?? compared.Target.Value,
                    baselineDatabase ?? compared.Target.Database ?? "",
                    request.BaselineProfile)
                : null);

        var anyNotPassed = results.Any(r => r.Outcome is TestOutcome.Failed or TestOutcome.Missing or TestOutcome.Error);
        return TomixResult<TestRunResult>.Ok(runResult, exitCode: anyNotPassed ? 1 : 0);
    }

    /// <summary>
    /// Runs one test query. A failed or truncated query comes back as an error message (with the
    /// result, when there is one, for its server and database); cancellation and authentication
    /// failures still throw and abort the run.
    /// </summary>
    private static async Task<(ModelQueryResult? Result, string? Error)> ExecuteAsync(
        IModelQuerySession session,
        ModelQueryRequest request,
        CancellationToken cancellationToken)
    {
        ModelQueryResult result;
        try
        {
            result = await session.ExecuteQueryAsync(request, traceWriter: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AuthenticationRequiredException)
        {
            var message = ex is InvalidOperationException ? ex.Message : ex.InnerException?.Message ?? ex.Message;
            return (null, QueryFailedPrefix + message);
        }

        return result.Truncated
            ? (result, $"Result exceeded --max-rows ({request.MaxRows}). Regression queries should return small, stable rowsets.")
            : (result, null);
    }

    /// <summary>
    /// Baseline mode: the baseline model's result stands in for the snapshot, compared exactly as
    /// one would be. An error on either side fails the test as an error naming that side.
    /// </summary>
    private static TestCaseResult CompareBaseline(
        DiscoveredTest test,
        ModelQueryResult? actual,
        string? actualError,
        ModelQueryResult? baseline,
        string? baselineError,
        string queryHash,
        TestCompareOptions compare,
        TestAssertions assertions,
        Stopwatch watch)
    {
        if (actualError is not null && baselineError is not null)
            return Case(test, TestOutcome.Error, watch,
                $"Actual model: {actualError}{Environment.NewLine}{Environment.NewLine}Baseline model: {baselineError}", side: TestSide.Both);
        if (actualError is not null)
            return Case(test, TestOutcome.Error, watch, $"Actual model: {actualError}", side: TestSide.Actual);
        if (baselineError is not null)
            return Case(test, TestOutcome.Error, watch, $"Baseline model: {baselineError}", side: TestSide.Baseline);

        // The assertions hold the tested model to the test's own rules; the baseline only to agreement.
        var comparison = TestResultComparer.Compare(TestSnapshotFile.FromResult(baseline!, queryHash), actual!, compare);
        return Verdict(test, assertions.Check(actual!), comparison, watch,
            $"{comparison.TotalDifferences} difference(s) from the baseline model.");
    }

    private static TestCaseResult CompareSnapshot(
        DiscoveredTest test,
        ModelQueryResult actual,
        string queryHash,
        TestCompareOptions compare,
        TestAssertions assertions,
        IReadOnlyList<string> assertionFailures,
        Stopwatch watch)
    {
        if (!File.Exists(test.ExpectedPath))
        {
            // A broken assertion outranks the missing snapshot: recording would not fix it.
            if (!assertions.UsesSnapshot || assertionFailures.Count > 0)
                return Verdict(test, assertionFailures, comparison: null, watch, differencesMessage: "");

            return Case(test, TestOutcome.Missing, watch, assertions.Snapshot
                ? "No snapshot recorded. Run 'tx test --update-missing' to record it."
                : "No snapshot recorded. Run 'tx test --update-missing' to record it, or check the result with a tx: rows or columns assertion instead.");
        }

        var snapshot = TestSnapshotFile.Load(test.ExpectedPath, out var loadError);
        if (snapshot is null)
            return Case(test, TestOutcome.Error, watch, loadError);

        var comparison = TestResultComparer.Compare(snapshot, actual, compare);
        var message = $"{comparison.TotalDifferences} difference(s).";
        if (!string.Equals(snapshot.QuerySha256, queryHash, StringComparison.Ordinal))
            message += " The query changed since the snapshot was recorded — run 'tx test --update' if intended.";

        return Verdict(test, assertionFailures, comparison, watch, message);
    }

    /// <summary>
    /// Passed when every assertion holds and the comparison (if any) found no difference;
    /// otherwise Failed, with the broken assertions first and then the differences.
    /// </summary>
    private static TestCaseResult Verdict(
        DiscoveredTest test,
        IReadOnlyList<string> assertionFailures,
        TestComparison? comparison,
        Stopwatch watch,
        string differencesMessage)
    {
        var differs = comparison is { Passed: false };
        if (assertionFailures.Count == 0 && !differs)
            return Case(test, TestOutcome.Passed, watch);

        var message = string.Join(" ", differs ? assertionFailures.Append(differencesMessage) : assertionFailures);
        return differs
            ? Case(test, TestOutcome.Failed, watch, message, comparison!.Differences, comparison.TotalDifferences)
            : Case(test, TestOutcome.Failed, watch, message);
    }

    /// <summary>
    /// An <c>expect-error</c> test passes when its query fails with a matching error. A query
    /// cut off by <c>--max-rows</c> still ran, so it counts as one that succeeded.
    /// </summary>
    private static TestCaseResult CheckExpectedError(
        DiscoveredTest test,
        ModelQueryResult? result,
        string? error,
        TestAssertions assertions,
        Stopwatch watch)
    {
        var queryError = result is null && error is not null
            ? error.StartsWith(QueryFailedPrefix, StringComparison.Ordinal) ? error[QueryFailedPrefix.Length..] : error
            : null;
        var failure = assertions.CheckError(result, queryError);
        return failure is null
            ? Case(test, TestOutcome.Passed, watch)
            : Case(test, TestOutcome.Failed, watch, failure);
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
            var existed = File.Exists(test.ExpectedPath);
            string? previousError = null;
            var previous = existed ? TestSnapshotFile.Load(test.ExpectedPath, out previousError) : null;
            if (!TestSnapshotFile.Save(test.ExpectedPath, TestSnapshotFile.FromResult(actual, queryHash)))
                return Case(test, TestOutcome.Unchanged, watch);

            if (!existed)
                return Case(test, TestOutcome.Updated, watch, "Recorded a new snapshot.");
            if (previous is null)
                return Case(test, TestOutcome.Updated, watch, $"Replaced an unreadable snapshot. {previousError}");

            // Exact, whatever the run's tolerance: this is what the rewrite changed in the file.
            var changes = TestResultComparer.Compare(previous, actual, TestCompareOptions.Exact);
            if (changes.Passed)
                return Case(test, TestOutcome.Updated, watch,
                    string.Equals(previous.QuerySha256, queryHash, StringComparison.Ordinal)
                        ? "Same results; rewrote the snapshot file's formatting."
                        : "Same results; recorded the changed query.");

            return Case(test, TestOutcome.Updated, watch,
                $"Accepted {changes.TotalDifferences} difference(s) from the previous snapshot.",
                changes.Differences, changes.TotalDifferences);
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
        int totalDifferences = 0,
        TestSide? side = null)
        => new(test.Name, test.DaxPath, outcome, watch.ElapsedMilliseconds, message, differences, totalDifferences, side);
}
