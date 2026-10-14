using System.Text.RegularExpressions;
using Tomix.App.Test;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Outcome mapping for the <c>tx test</c> pipeline projections: TRX outcomes per
/// <see cref="TestOutcome"/> and CI annotations for non-passing tests only; plus the text
/// report's handling of re-recorded snapshots and orphans.
/// In the console-state collection because the EmitCi tests swap <c>Console.Error</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class TestRunRendererTests
{
    private static TestCaseResult Case(
        TestOutcome outcome,
        string name = "totals/sales",
        string? message = null,
        IReadOnlyList<TestDifference>? differences = null,
        int totalDifferences = 0)
        => new(name, $"/tests/{name}.dax", outcome, DurationMs: 1, message, differences, totalDifferences);

    private static TestRunResult Run(params TestCaseResult[] tests) => new(
        "server", "db", "./tests", tests,
        Passed: tests.Count(t => t.Outcome == TestOutcome.Passed),
        Failed: tests.Count(t => t.Outcome == TestOutcome.Failed),
        Missing: tests.Count(t => t.Outcome == TestOutcome.Missing),
        Errored: tests.Count(t => t.Outcome == TestOutcome.Error),
        Updated: tests.Count(t => t.Outcome == TestOutcome.Updated),
        DurationMs: 5);

    [Theory]
    [InlineData(TestOutcome.Passed, "Passed")]
    [InlineData(TestOutcome.Updated, "Passed")]
    [InlineData(TestOutcome.Unchanged, "Passed")]
    [InlineData(TestOutcome.Failed, "Failed")]
    [InlineData(TestOutcome.Missing, "Failed")]
    [InlineData(TestOutcome.Error, "Error")]
    public void ToTrxTests_MapsOutcomes(TestOutcome outcome, string expected)
    {
        var trxTest = Assert.Single(TestRunRenderer.ToTrxTests(Run(Case(outcome, message: "why"))));

        Assert.Equal("totals/sales", trxTest.Name);
        Assert.Equal(expected, trxTest.Outcome.ToString());
    }

    [Fact]
    public void ToTrxTests_FailedTest_MessageIncludesDifferences()
    {
        var run = Run(Case(
            TestOutcome.Failed,
            message: "1 difference(s).",
            differences: [new TestDifference("cell", Row: 2, Column: "[Total]", Expected: "1", Actual: null)],
            totalDifferences: 1));

        var trxTest = Assert.Single(TestRunRenderer.ToTrxTests(run));

        Assert.Contains("1 difference(s).", trxTest.Message);
        Assert.Contains("row 2, [Total]: expected '1', actual (blank)", trxTest.Message);
    }

    [Fact]
    public void ToTrxTests_PassedTest_HasNoMessage()
        => Assert.Null(Assert.Single(TestRunRenderer.ToTrxTests(Run(Case(TestOutcome.Passed)))).Message);

    [Fact]
    public void EmitCi_AnnotatesOnlyNonPassingTests_AsErrors()
    {
        var run = Run(
            Case(TestOutcome.Passed, name: "a-pass"),
            Case(TestOutcome.Failed, name: "b-fail", message: "1 difference(s).",
                differences: [new TestDifference("cell", 1, "[Total]", "1", "2")], totalDifferences: 1),
            Case(TestOutcome.Missing, name: "c-miss", message: "No snapshot recorded."));

        var captured = ConsoleCapture.Run(() => TestRunRenderer.EmitCi("github", run));

        var lines = captured.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("::error::", line));
        Assert.Contains("b-fail", lines[0]);
        Assert.Contains("row 1, [Total]: expected '1', actual '2'", lines[0]);
        Assert.Contains("c-miss", lines[1]);
    }

    [Fact]
    public void EmitCi_NoAnnotations_WhenAllPass()
    {
        var captured = ConsoleCapture.Run(
            () => TestRunRenderer.EmitCi("vsts", Run(Case(TestOutcome.Passed))));

        Assert.Equal("", captured.Stderr);
    }

    [Fact]
    public void Render_UpdatedTest_ShowsWhatTheRewriteChanged()
    {
        var output = Render(Run(Case(
            TestOutcome.Updated,
            message: "Accepted 1 difference(s) from the previous snapshot.",
            differences: [new TestDifference("cell", Row: 1, Column: "[Total]", Expected: "100", Actual: "120")],
            totalDifferences: 1)));

        Assert.Contains("UPDATED totals/sales", output);
        Assert.Contains("Accepted 1 difference(s) from the previous snapshot.", output);
        Assert.Contains("Before", output);
        Assert.Contains("After", output);
        Assert.Contains("100", output);
        Assert.Contains("120", output);
    }

    [Fact]
    public void Render_ListsOrphanedSnapshots()
    {
        var output = Render(Run(Case(TestOutcome.Passed)) with
        {
            Orphans = [new OrphanedSnapshot("totals/old", "/tests/totals/old.expected.json")]
        }, stderr: true);

        Assert.Contains("1 snapshot(s) have no .dax test", output);
        Assert.Contains("totals/old.expected.json", output);
    }

    [Fact]
    public void Render_PointsMissingTestsAtUpdateMissing()
        => Assert.Contains("tx test --update-missing", Render(Run(Case(TestOutcome.Missing)), stderr: true));

    private static string Render(TestRunResult result, bool stderr = false)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                TestRunRenderer.Render(result, quiet: false);
                return 0;
            },
            captureAnsiConsole: true);
        var text = stderr ? captured.Stderr : captured.Stdout;
        return Regex.Replace(Regex.Replace(text, @"\x1b\[[0-9;]*m", ""), @"\s+", " ");
    }
}
