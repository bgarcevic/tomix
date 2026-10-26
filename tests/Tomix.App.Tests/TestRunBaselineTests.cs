using Tomix.App.State;
using Tomix.App.Test;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// <c>tx test --baseline</c>: every query runs on the tested model and on a baseline model, and
/// the baseline's result takes the snapshot's place. Snapshots are neither read nor written.
/// </summary>
public sealed class TestRunBaselineTests : IDisposable
{
    private const string Workspace = "powerbi://api.powerbi.com/v1.0/myorg/ws";

    private static readonly Func<CliConnectionState?> RemoteState =
        () => new CliConnectionState(
            Server: Workspace,
            Database: "MyModel",
            Model: null,
            Auth: null,
            Local: false,
            Profile: null,
            Workspace: null);

    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-testbaseline-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteDax(string name, string content = "EVALUATE 'Sales'")
        => File.WriteAllText(Path.Combine(_dir, name + ".dax"), content);

    private string SnapshotPath(string name) => Path.Combine(_dir, name + ".expected.json");

    private static QueryStubs.Session Answering(params object?[] values)
        => new() { OnQuery = _ => QueryStubs.Rowset(values) };

    private static QueryStubs.Session Throwing(string message)
        => new() { Throw = new InvalidOperationException(message) };

    /// <summary>The baseline is the model named "Baseline"; every other reference is the tested model.</summary>
    private static QueryStubs.RoutingProvider Provider(QueryStubs.Session? actual, QueryStubs.Session? baseline)
        => new(reference => reference.Database == "Baseline" ? baseline : actual);

    private TestRunRequest Request(
        string? baselineServer = null,
        string? baselineDatabase = "Baseline",
        string? baselineProfile = null,
        bool update = false,
        bool updateMissing = false,
        TestCompareOptions? compare = null,
        int maxRows = 10000)
        => new(Model: null, Server: null, Database: null, Auth: null, Path: _dir,
            Update: update, MaxRows: maxRows, Compare: compare, UpdateMissing: updateMissing,
            BaselineServer: baselineServer, BaselineDatabase: baselineDatabase, BaselineProfile: baselineProfile);

    private static Task<Tomix.Core.Results.TomixResult<TestRunResult>> Run(
        QueryStubs.RoutingProvider provider, TestRunRequest request)
        => new TestRunHandler([provider], RemoteState).HandleAsync(request, CancellationToken.None);

    // ── Outcomes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Baseline_Passes_WhenBothModelsReturnTheSameRows()
    {
        WriteDax("sales");
        var result = await Run(Provider(Answering(1L, 2L), Answering(1L, 2L)), Request());

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Passed, test.Outcome);
        Assert.Null(test.Side);
        Assert.Equal(0, result.Data.Missing);
    }

    [Fact]
    public async Task Baseline_Fails_WithTheBaselineAsTheExpectedSide()
    {
        WriteDax("sales");
        var result = await Run(Provider(actual: Answering(2L), baseline: Answering(1L)), Request());

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Failed, test.Outcome);
        Assert.Contains("from the baseline model", test.Message);
        var difference = Assert.Single(test.Differences!);
        Assert.Equal(("cell", "1", "2"), (difference.Kind, difference.Expected, difference.Actual));
    }

    [Theory]
    [InlineData(true, false, TestSide.Actual, "Actual model: Query failed: dev broke")]
    [InlineData(false, true, TestSide.Baseline, "Baseline model: Query failed: prod broke")]
    [InlineData(true, true, TestSide.Both, "Actual model: Query failed: dev broke\n\nBaseline model: Query failed: prod broke")]
    public async Task Baseline_Errors_NameTheSideThatFailed(
        bool actualFails, bool baselineFails, TestSide side, string message)
    {
        WriteDax("sales");
        var provider = Provider(
            actualFails ? Throwing("dev broke") : Answering(1L),
            baselineFails ? Throwing("prod broke") : Answering(1L));

        var result = await Run(provider, Request());

        Assert.Equal(1, result.ExitCode);
        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Error, test.Outcome);
        Assert.Equal(side, test.Side);
        Assert.Equal(message.Replace("\n", Environment.NewLine), test.Message);
    }

    [Fact]
    public async Task Baseline_TruncatedBaselineResult_IsABaselineError()
    {
        WriteDax("sales");
        var baseline = new QueryStubs.Session { OnQuery = _ => QueryStubs.Rowset(1L) with { Truncated = true } };

        var result = await Run(Provider(Answering(1L), baseline), Request(maxRows: 5));

        var test = Assert.Single(result.Data!.Tests);
        Assert.Equal(TestOutcome.Error, test.Outcome);
        Assert.Equal(TestSide.Baseline, test.Side);
        Assert.Contains("--max-rows (5)", test.Message);
    }

    [Fact]
    public async Task Baseline_AppliesTheRunTolerance()
    {
        WriteDax("sales");
        var result = await Run(
            Provider(Answering(100.5), Answering(100.0)),
            Request(compare: new TestCompareOptions(new TestTolerance(1, Relative: false))));

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
    }

    [Fact]
    public async Task Baseline_AppliesPerTestDirectives()
    {
        WriteDax("sales", "// tx: ignore-row-order\nEVALUATE 'Sales'");
        var result = await Run(Provider(Answering(2L, 1L), Answering(1L, 2L)), Request());

        Assert.Equal(TestOutcome.Passed, Assert.Single(result.Data!.Tests).Outcome);
    }

    [Fact]
    public async Task Baseline_KeepsDiscoveryOrder()
    {
        WriteDax("c");
        WriteDax("a");
        WriteDax("b");
        var result = await Run(Provider(Answering(1L), Answering(1L)), Request());

        Assert.Equal(["a", "b", "c"], result.Data!.Tests.Select(t => t.Name));
    }

    // ── Snapshots are out of the picture ────────────────────────────────────

    [Fact]
    public async Task Baseline_IgnoresAndNeverWritesSnapshots()
    {
        WriteDax("sales");
        WriteDax("new-test");
        File.WriteAllText(SnapshotPath("sales"), "{ not json");
        File.WriteAllText(SnapshotPath("orphan"), "{}");

        var result = await Run(Provider(Answering(1L), Answering(1L)), Request());

        Assert.All(result.Data!.Tests, test => Assert.Equal(TestOutcome.Passed, test.Outcome));
        Assert.Empty(result.Data.Orphans!);
        Assert.Equal("{ not json", File.ReadAllText(SnapshotPath("sales")));
        Assert.False(File.Exists(SnapshotPath("new-test")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Baseline_RejectsUpdateModes(bool update, bool updateMissing)
    {
        WriteDax("sales");
        var actual = Answering(1L);
        var result = await Run(Provider(actual, Answering(1L)), Request(update: update, updateMissing: updateMissing));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_CONFLICTING_OPTIONS", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.Null(actual.LastRequest);
        Assert.False(File.Exists(SnapshotPath("sales")));
    }

    // ── Targets ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Baseline_DatabaseAlone_UsesTheTestedModelsServer()
    {
        WriteDax("sales");
        var provider = Provider(Answering(1L), Answering(1L));
        await Run(provider, Request());

        Assert.Equal(2, provider.Opened.Count);
        Assert.Equal(new ModelReference(Workspace, "MyModel"), provider.Opened[0]);
        Assert.Equal(new ModelReference(Workspace, "Baseline"), provider.Opened[1]);
    }

    [Fact]
    public async Task Baseline_Server_IsNormalizedAndKeepsTheTestedDatabase()
    {
        WriteDax("sales");
        var provider = new QueryStubs.RoutingProvider(_ => Answering(1L));
        var result = await Run(provider, Request(baselineServer: "Prod", baselineDatabase: null, baselineProfile: "prod"));

        Assert.Equal(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Prod", "MyModel"), provider.Opened[1]);
        Assert.Equal(new TestBaselineTarget("stub-server", "stub-db", "prod"), result.Data!.Baseline);
    }

    [Fact]
    public async Task SnapshotRun_HasNoBaselineTarget()
    {
        WriteDax("sales");
        var provider = new QueryStubs.RoutingProvider(_ => Answering(1L));
        var result = await Run(provider, Request(baselineDatabase: null, update: true));

        Assert.Null(result.Data!.Baseline);
        Assert.Single(provider.Opened);
    }

    [Fact]
    public async Task Baseline_ThatCannotQuery_IsUnsupported()
    {
        WriteDax("sales");
        var result = await Run(Provider(Answering(1L), baseline: null), Request());

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Contains("baseline", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task Baseline_ConnectionFailure_NamesTheBaseline()
    {
        WriteDax("sales");
        var provider = new ThrowingOnBaselineProvider(Answering(1L));
        var result = await new TestRunHandler([provider], RemoteState).HandleAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DATABASE_REQUIRED", result.Diagnostics[0].Code);
        Assert.StartsWith("Baseline model: ", result.Diagnostics[0].Message);
    }

    // ── --baseline <profile> ────────────────────────────────────────────────

    [Theory]
    [InlineData("prod", null)]
    [InlineData("files", "TOMIX_TEST_BASELINE_NO_SERVER")]
    [InlineData("nope", "TOMIX_PROFILE_NOT_FOUND")]
    public void BaselineProfile_MustNameALiveModel(string name, string? code)
    {
        var store = new CliStateStore(Path.Combine(_dir, "state"));
        store.SaveProfiles(new Dictionary<string, CliProfile>
        {
            ["prod"] = new("prod", "Prod", "Sales", Model: null, Auth: null, Description: null),
            ["files"] = new("files", Server: null, Database: null, Model: "./model", Auth: null, Description: null, Local: true)
        });

        var result = TestBaselineProfileResolver.Resolve(store, name);

        Assert.Equal(code, result.Success ? null : result.Diagnostics[0].Code);
        if (code is null)
            Assert.Equal(("Prod", "Sales"), (result.Data!.Server, result.Data.Database));
        else
            Assert.Equal(2, result.ExitCode);
    }

    private sealed class ThrowingOnBaselineProvider(QueryStubs.Session actual) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => reference.Database == "Baseline"
                ? throw new ModelConnectionException(ModelConnectionFailureKind.DatabaseRequired, "no such model")
                : Task.FromResult<IModelSession>(actual);
    }
}
