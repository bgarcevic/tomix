using Tomix.App.Test;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Outcome mapping for the <c>tx test</c> pipeline projections: TRX outcomes per
/// <see cref="TestOutcome"/> and CI annotations for non-passing tests only.
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

        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            TestRunRenderer.EmitCi("github", run);
        }
        finally
        {
            Console.SetError(original);
        }

        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("::error::", line));
        Assert.Contains("b-fail", lines[0]);
        Assert.Contains("row 1, [Total]: expected '1', actual '2'", lines[0]);
        Assert.Contains("c-miss", lines[1]);
    }

    [Fact]
    public void EmitCi_NoAnnotations_WhenAllPass()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            TestRunRenderer.EmitCi("vsts", Run(Case(TestOutcome.Passed)));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }
}
