using Tomix.App.State;
using Tomix.App.Test;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// <c>tx:</c> assertion directives in <c>tx test</c>: <c>rows=</c>, <c>rows&gt;=</c> and
/// <c>columns=</c> check the result without a snapshot, and <c>expect-error</c> requires the
/// query to fail.
/// </summary>
public sealed class TestRunAssertionTests : IDisposable
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

    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-testassert-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteDax(string name, string header)
        => File.WriteAllText(Path.Combine(_dir, name + ".dax"), header + "\nEVALUATE 'Sales'");

    private string SnapshotPath(string name) => Path.Combine(_dir, name + ".expected.json");

    private static QueryStubs.Session Answering(params object?[] values)
        => new() { OnQuery = _ => QueryStubs.Rowset(values) };

    private static QueryStubs.Session Throwing(string message)
        => new() { Throw = new InvalidOperationException(message) };

    private static QueryStubs.Session Returning(ModelQueryResult result) => new() { Result = result };

    private Task<Tomix.Core.Results.TomixResult<TestRunResult>> Run(
        QueryStubs.Session session, bool update = false, bool updateMissing = false, int maxRows = 10000,
        string? baselineDatabase = null, QueryStubs.Session? baseline = null)
    {
        var request = new TestRunRequest(Model: null, Server: null, Database: null, Auth: null, Path: _dir,
            Update: update, MaxRows: maxRows, UpdateMissing: updateMissing, BaselineDatabase: baselineDatabase);
        var provider = new QueryStubs.RoutingProvider(reference => reference.Database == "Baseline" ? baseline : session);
        return new TestRunHandler([provider], RemoteState).HandleAsync(request, CancellationToken.None);
    }

    // ── Row and column assertions ───────────────────────────────────────────

    [Theory]
    [InlineData("// tx: rows=2", 2, true)]
    [InlineData("// tx: rows=3", 2, false)]
    [InlineData("// tx: rows>=1", 2, true)]
    [InlineData("// tx: rows>=1", 0, false)]
    [InlineData("// tx: rows>=0", 0, true)]
    [InlineData("// tx: rows>=1 rows=0", 0, false)]
    [InlineData("// tx: rows<=2", 2, true)]
    [InlineData("// tx: rows<=2", 3, false)]
    [InlineData("// tx: rows<=0", 0, true)]
    [InlineData("// tx: rows>=1 rows<=3", 2, true)]
    public async Task RowAssertions_PassOrFail_WithoutASnapshot(string header, int rowCount, bool passes)
    {
        WriteDax("sales", header);
        var result = await Run(Answering(Enumerable.Range(0, rowCount).Select(i => (object?)(long)i).ToArray()));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(passes ? TestOutcome.Passed : TestOutcome.Failed, test.Outcome);
        Assert.Equal(passes ? 0 : 1, result.ExitCode);
        Assert.Equal(0, result.Data.Missing);
        if (!passes)
            Assert.Contains($"got {rowCount}", test.Message);
    }

    [Theory]
    [InlineData("// tx: columns=Sales[Region], [Total Sales]")]
    [InlineData("// tx: columns='Sales'[Region]")]
    [InlineData("// tx: columns=sales[region],[total sales]")]
    [InlineData("// tx: ignore-row-order columns=[Total Sales]")]
    public async Task Columns_Pass_WhenEveryNamedColumnIsPresent(string header)
    {
        WriteDax("sales", header);
        var result = await Run(Returning(RegionSales()));

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
    }

    [Fact]
    public async Task Columns_Fail_NamingTheMissingOnesAndWhatTheResultHas()
    {
        WriteDax("sales", "// tx: columns=Sales[Region], [Margin %]");
        var result = await Run(Returning(RegionSales()));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Contains("Missing column(s): [Margin %].", test.Message);
        Assert.Contains("The result has: Sales[Region], [Total Sales].", test.Message);
    }

    [Fact]
    public async Task Assertions_ReportEveryBrokenOne()
    {
        WriteDax("sales", "// tx: rows=5\n// tx: columns=[Margin]");
        var result = await Run(Returning(RegionSales()));

        var message = Assert.Single(result.Data!.Tests).Message;
        Assert.Contains("Expected 5 row(s), got 1.", message);
        Assert.Contains("Missing column(s): [Margin].", message);
    }

    [Fact]
    public async Task Assertions_AndASnapshot_BothApply()
    {
        WriteDax("sales", "// tx: rows=1");
        await Run(Answering(1L), update: true);
        Assert.False(File.Exists(SnapshotPath("sales")));

        // Record one by hand, as a test that had a snapshot before it gained assertions would.
        TestSnapshotFile.Save(SnapshotPath("sales"), TestSnapshotFile.FromResult(QueryStubs.Rowset(1L), "hash"));

        var result = await Run(Answering(2L));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("1 difference(s). The query changed since the snapshot was recorded — run 'tx test --update' if intended.", test.Message);
        Assert.Single(test.Differences!);
    }

    [Fact]
    public async Task UpdateMissing_RecordsNoSnapshot_ForAnAssertedTest()
    {
        WriteDax("sales", "// tx: rows>=1");
        var result = await Run(Answering(1L), updateMissing: true);

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
        Assert.False(File.Exists(SnapshotPath("sales")));
    }

    [Fact]
    public async Task Update_ReRecordsAnAssertedTestsSnapshot_OnlyWhileItsAssertionsHold()
    {
        WriteDax("sales", "// tx: rows=1");
        TestSnapshotFile.Save(SnapshotPath("sales"), TestSnapshotFile.FromResult(QueryStubs.Rowset(1L), "hash"));
        var before = File.ReadAllText(SnapshotPath("sales"));

        var failing = await Run(Answering(1L, 2L), update: true);
        Assert.Equal(TestOutcome.Failed, Assert.Single(failing.Data!.Tests).Outcome);
        Assert.Equal(1, failing.ExitCode);
        Assert.Equal(before, File.ReadAllText(SnapshotPath("sales")));

        var passing = await Run(Answering(2L), update: true);
        Assert.Equal(TestOutcome.Updated, Assert.Single(passing.Data!.Tests).Outcome);
        Assert.Contains("\"2\"", File.ReadAllText(SnapshotPath("sales")));
    }

    // ── snapshot ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_MakesAnAssertedTestMissing_UntilItIsRecorded()
    {
        WriteDax("sales", "// tx: rows>=1 snapshot");
        var result = await Run(Answering(1L));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Missing, test.Outcome);
        Assert.Equal("No snapshot recorded. Run 'tx test --update-missing' to record it.", test.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Snapshot_LetsRecordingCreateOne_ForAnAssertedTest(bool update, bool updateMissing)
    {
        WriteDax("sales", "// tx: rows>=1\n// tx: snapshot");
        var recorded = await Run(Answering(1L), update: update, updateMissing: updateMissing);

        Assert.Equal(TestOutcome.Updated, Assert.Single(recorded.Data!.Tests).Outcome);
        Assert.True(File.Exists(SnapshotPath("sales")));

        // From then on both apply: the assertion holds, the snapshot catches the changed value.
        var result = await Run(Answering(2L));
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("1 difference(s).", test.Message);
    }

    [Fact]
    public async Task Snapshot_RecordsNothing_WhileAnAssertionFails()
    {
        WriteDax("sales", "// tx: rows>=1 snapshot");
        var result = await Run(Answering(), updateMissing: true);

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("Expected at least 1 row(s), got 0.", test.Message);
        Assert.False(File.Exists(SnapshotPath("sales")));
    }

    [Fact]
    public async Task Snapshot_ChangesNothing_ForATestWithoutAssertions()
    {
        WriteDax("sales", "// tx: snapshot");

        Assert.Equal(TestOutcome.Missing, Assert.Single((await Run(Answering(1L))).Data!.Tests).Outcome);
        Assert.Equal(TestOutcome.Updated, Assert.Single((await Run(Answering(1L), updateMissing: true)).Data!.Tests).Outcome);
    }

    [Fact]
    public async Task Assertions_ApplyToTheTestedModel_InABaselineRun()
    {
        WriteDax("sales", "// tx: rows>=1");
        var result = await Run(Answering(), baselineDatabase: "Baseline", baseline: Answering());

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("Expected at least 1 row(s), got 0.", test.Message);
        Assert.Null(test.Differences);
    }

    // ── expect-error ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("// tx: expect-error")]
    [InlineData("// tx: expect-error=cannot be found")]
    [InlineData("// tx: expect-error=COLUMN '.*' CANNOT")]
    [InlineData("// tx: ignore-row-order expect-error=^Column 'Foo'")]
    public async Task ExpectError_Passes_WhenTheQueryFailsWithAMatchingError(string header)
    {
        WriteDax("broken", header);
        var result = await Run(Throwing("Column 'Foo' cannot be found in table 'Sales'."));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Passed, test.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(SnapshotPath("broken")));
    }

    [Fact]
    public async Task ExpectError_Fails_WhenTheErrorDoesNotMatch()
    {
        WriteDax("broken", "// tx: expect-error=circular dependency");
        var result = await Run(Throwing("Column 'Foo' cannot be found."));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("The query failed, but its error does not match 'circular dependency': Column 'Foo' cannot be found.", test.Message);
    }

    [Fact]
    public async Task ExpectError_Fails_WhenTheQuerySucceeds()
    {
        WriteDax("broken", "// tx: expect-error");
        var result = await Run(Answering(1L, 2L));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Equal("Expected the query to fail, but it returned 2 row(s).", test.Message);
    }

    [Fact]
    public async Task ExpectError_CountsATruncatedQuery_AsOneThatSucceeded()
    {
        WriteDax("broken", "// tx: expect-error");
        var result = await Run(Returning(QueryStubs.Rowset(1L) with { Truncated = true }), maxRows: 1);

        Assert.Equal(TestOutcome.Failed, Assert.Single(result.Data!.Tests).Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExpectError_NeverRecordsASnapshot(bool update, bool updateMissing)
    {
        WriteDax("broken", "// tx: expect-error");
        var result = await Run(Throwing("nope"), update: update, updateMissing: updateMissing);

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
        Assert.False(File.Exists(SnapshotPath("broken")));
    }

    [Fact]
    public async Task ExpectError_RunsOnlyOnTheTestedModel_InABaselineRun()
    {
        WriteDax("broken", "// tx: expect-error");
        var baseline = Answering(1L);
        var result = await Run(Throwing("gone"), baselineDatabase: "Baseline", baseline: baseline);

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
        Assert.Null(baseline.LastRequest);
    }

    private static ModelQueryResult RegionSales() => new(
        "stub-server",
        "stub-db",
        [new QueryColumn("Sales[Region]", "string"), new QueryColumn("[Total Sales]", "decimal")],
        [["East", 10m]],
        Truncated: false,
        DurationMs: 1);
}
