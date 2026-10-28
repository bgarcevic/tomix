using Tomix.Core.Results;

namespace Tomix.App.Test;

/// <param name="Path">Test file or directory, discovered as <c>tx test</c> does.</param>
/// <param name="Filter">Optional <c>*</c>/<c>?</c> wildcard on test names.</param>
/// <param name="Tags">Optional tags; a test is listed when it has any of them.</param>
public sealed record TestListRequest(string Path, string? Filter = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// One discovered test as <c>tx test</c> would run it. Property names and order are the
/// documented JSON contract (additive changes only).
/// </summary>
/// <param name="Name">Test name (relative path without extension, '/'-normalized).</param>
/// <param name="File">Full path of the <c>.dax</c> file.</param>
/// <param name="Tags">The test's <c>tags=</c>; empty when it has none.</param>
/// <param name="Checks">What decides the test: <c>snapshot</c>, <c>assertions</c>,
/// <c>assertions+snapshot</c> or <c>expect-error</c>; null when its directives are invalid.</param>
/// <param name="HasSnapshot">Whether its <c>.expected.json</c> exists.</param>
/// <param name="Error">Why the test would fail as an error before running (unreadable file, bad directive); null otherwise.</param>
public sealed record TestListItem(
    string Name,
    string File,
    IReadOnlyList<string> Tags,
    string? Checks,
    bool HasSnapshot,
    string? Error);

public sealed record TestListResult(string Path, IReadOnlyList<TestListItem> Tests);

/// <summary>
/// Lists the tests <c>tx test</c> would run, offline: no model is opened, so it answers "what is
/// in this folder, how is each test decided, and which tags select it".
/// </summary>
public sealed class TestListHandler
{
    public TomixResult<TestListResult> Handle(TestListRequest request)
    {
        if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
            return TomixResult<TestListResult>.Fail(
                "TOMIX_TEST_PATH_NOT_FOUND",
                $"Test path not found: {request.Path}",
                exitCode: 2,
                hint: "Pass a .dax file or a directory containing .dax test files, or create one with 'tx test init'.");

        var items = new List<TestListItem>();
        foreach (var test in TestDiscovery.Discover(request.Path)
                     .Where(test => TestDiscovery.MatchesFilter(test.Name, request.Filter)))
        {
            var hasSnapshot = File.Exists(test.ExpectedPath);
            string query;
            try
            {
                query = File.ReadAllText(test.DaxPath);
            }
            catch (IOException ex)
            {
                // Kept under --tag too, as tx test runs it (TestTags.Select): its tags cannot be read.
                items.Add(new TestListItem(test.Name, test.DaxPath, [], null, hasSnapshot, $"Query file could not be read: {ex.Message}"));
                continue;
            }

            var directives = TestCompareOptions.Exact.ReadDirectives(query, out var error);
            if (directives is null)
            {
                items.Add(new TestListItem(test.Name, test.DaxPath, [], null, hasSnapshot, error));
                continue;
            }

            if (TestTags.Matches(directives.Tags, request.Tags))
                items.Add(new TestListItem(test.Name, test.DaxPath, directives.Tags, Checks(directives.Assertions), hasSnapshot, null));
        }

        return TomixResult<TestListResult>.Ok(new TestListResult(request.Path, items));
    }

    private static string Checks(TestAssertions assertions)
        => assertions switch
        {
            { ExpectError: true } => "expect-error",
            { ChecksResult: true, Snapshot: true } => "assertions+snapshot",
            { ChecksResult: true } => "assertions",
            _ => "snapshot",
        };
}
