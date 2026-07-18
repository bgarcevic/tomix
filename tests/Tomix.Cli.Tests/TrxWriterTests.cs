using System.Xml.Linq;
using Tomix.App.Bpa;
using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Tests;

public sealed class TrxWriterTests : IDisposable
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-trx-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private XDocument Write(IReadOnlyList<TrxWriter.TrxTest> tests, string name = "run.trx")
    {
        var path = Path.Combine(_dir, name);
        TrxWriter.Write(path, "tx test-run", tests);
        return XDocument.Load(path);
    }

    [Fact]
    public void Write_EmitsDefinitionsResultsEntriesAndCounters()
    {
        var doc = Write(
        [
            new TrxWriter.TrxTest("Rule A", TrxWriter.TrxOutcome.Failed, "object 'T'[M]"),
            new TrxWriter.TrxTest("Rule B", TrxWriter.TrxOutcome.Passed),
            new TrxWriter.TrxTest("Rule C", TrxWriter.TrxOutcome.Warning, "watch out"),
            new TrxWriter.TrxTest("Rule D", TrxWriter.TrxOutcome.Error, "did not compile")
        ]);

        var root = doc.Root!;
        Assert.Equal(Ns + "TestRun", root.Name);
        Assert.Equal("tx test-run", root.Attribute("name")!.Value);

        var results = root.Element(Ns + "Results")!.Elements(Ns + "UnitTestResult").ToList();
        var definitions = root.Element(Ns + "TestDefinitions")!.Elements(Ns + "UnitTest").ToList();
        var entries = root.Element(Ns + "TestEntries")!.Elements(Ns + "TestEntry").ToList();
        Assert.Equal(4, results.Count);
        Assert.Equal(4, definitions.Count);
        Assert.Equal(4, entries.Count);

        // Every result's testId/executionId must resolve to a definition and an entry.
        foreach (var result in results)
        {
            var testId = result.Attribute("testId")!.Value;
            var executionId = result.Attribute("executionId")!.Value;
            Assert.Contains(definitions, d => d.Attribute("id")!.Value == testId);
            Assert.Contains(entries, e =>
                e.Attribute("testId")!.Value == testId &&
                e.Attribute("executionId")!.Value == executionId);
        }

        var failed = results.Single(r => r.Attribute("outcome")!.Value == "Failed");
        Assert.Equal("Rule A", failed.Attribute("testName")!.Value);
        Assert.Equal("object 'T'[M]",
            failed.Element(Ns + "Output")!.Element(Ns + "ErrorInfo")!.Element(Ns + "Message")!.Value);

        var counters = root.Element(Ns + "ResultSummary")!.Element(Ns + "Counters")!;
        Assert.Equal("4", counters.Attribute("total")!.Value);
        Assert.Equal("1", counters.Attribute("passed")!.Value);
        Assert.Equal("1", counters.Attribute("failed")!.Value);
        Assert.Equal("1", counters.Attribute("warning")!.Value);
        Assert.Equal("1", counters.Attribute("error")!.Value);
        Assert.Equal("Failed", root.Element(Ns + "ResultSummary")!.Attribute("outcome")!.Value);
    }

    [Fact]
    public void Write_AllPassed_SummaryOutcomeIsCompleted()
    {
        var doc = Write([new TrxWriter.TrxTest("Rule A", TrxWriter.TrxOutcome.Passed)]);

        Assert.Equal("Completed", doc.Root!.Element(Ns + "ResultSummary")!.Attribute("outcome")!.Value);
    }

    [Fact]
    public void Write_TestIds_AreDeterministicAcrossRuns()
    {
        TrxWriter.TrxTest[] tests = [new("Rule A", TrxWriter.TrxOutcome.Failed, "msg")];
        var first = Write(tests, "first.trx");
        var second = Write(tests, "second.trx");

        static string TestId(XDocument doc) => doc.Root!
            .Element(Ns + "Results")!.Element(Ns + "UnitTestResult")!.Attribute("testId")!.Value;

        Assert.Equal(TestId(first), TestId(second));
    }

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deep", "run.trx");
        TrxWriter.Write(path, "tx test-run", [new TrxWriter.TrxTest("A", TrxWriter.TrxOutcome.Passed)]);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ValidateProjection_MapsErrorsWarningsAndExpression()
    {
        var result = new ValidateModelResult(
            Valid: false,
            DurationMs: 1,
            Errors: [new ValidationIssue("TOMIX_DAX_ERROR", "Unknown column", "Sales[M]", "SUM('X'[Y])")],
            Warnings: [new ValidationIssue("TOMIX_DAX_WARNING", "Deprecated function", "Sales[N]", null)]);

        var tests = ValidateRenderer.ToTrxTests(result);

        Assert.Equal(2, tests.Count);
        Assert.Equal("Sales[M] (TOMIX_DAX_ERROR)", tests[0].Name);
        Assert.Equal(TrxWriter.TrxOutcome.Failed, tests[0].Outcome);
        Assert.Contains("Unknown column", tests[0].Message);
        Assert.Contains("SUM('X'[Y])", tests[0].Message);
        Assert.Equal(TrxWriter.TrxOutcome.Warning, tests[1].Outcome);
        Assert.Equal("Deprecated function", tests[1].Message);
    }

    [Fact]
    public void ValidateProjection_CleanModel_YieldsSinglePassedTest()
    {
        var result = new ValidateModelResult(Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var test = Assert.Single(ValidateRenderer.ToTrxTests(result));
        Assert.Equal(TrxWriter.TrxOutcome.Passed, test.Outcome);
    }

    [Fact]
    public void BpaProjection_GroupsViolationsPerRule_AndMapsSentinelsToError()
    {
        var v1 = new BpaViolation("R1", "Avoid floats", "Performance", BpaSeverity.Warning,
            "Column", "Amount", "tables/Sales/columns/Amount", "Use fixed decimal.");
        var v2 = v1 with { ObjectName = "Qty", ObjectPath = "tables/Sales/columns/Qty" };
        var result = new BpaRunResult(
            Results:
            [
                new BpaResult(BpaResultKind.Violation, "R1", "Avoid floats", "Performance", BpaSeverity.Warning, Violation: v1),
                new BpaResult(BpaResultKind.Violation, "R1", "Avoid floats", "Performance", BpaSeverity.Warning, Violation: v2),
                new BpaResult(BpaResultKind.Violation, "R1", "Avoid floats", "Performance", BpaSeverity.Warning,
                    Violation: v1 with { ObjectPath = "tables/Sales/columns/Ignored" }, IsIgnored: true),
                new BpaResult(BpaResultKind.CompilationError, "R2", "Broken rule", "Meta", BpaSeverity.Error,
                    ErrorMessage: "syntax error", ErrorScope: "Measure"),
                new BpaResult(BpaResultKind.EvaluationError, "R2", "Broken rule", "Meta", BpaSeverity.Error,
                    ErrorMessage: "cast failed", ErrorScope: "Column")
            ],
            ModelName: "m",
            RulesEvaluated: 5);

        var tests = BpaRunRenderer.ToTrxTests(result);

        Assert.Equal(2, tests.Count);
        var failed = Assert.Single(tests, t => t.Outcome == TrxWriter.TrxOutcome.Failed);
        Assert.Equal("Avoid floats [R1]", failed.Name);
        Assert.Contains("tables/Sales/columns/Amount", failed.Message);
        Assert.Contains("tables/Sales/columns/Qty", failed.Message);
        Assert.DoesNotContain("Ignored", failed.Message); // object-level ignores stay suppressed

        // Per-scope sentinels collapse into one Error test per rule.
        var error = Assert.Single(tests, t => t.Outcome == TrxWriter.TrxOutcome.Error);
        Assert.Equal("Broken rule [R2]", error.Name);
        Assert.Contains("Measure: syntax error", error.Message);
        Assert.Contains("Column: cast failed", error.Message);
    }

    [Fact]
    public void BpaProjection_CleanRun_YieldsSinglePassedTestWithRuleCount()
    {
        var result = new BpaRunResult(Results: [], ModelName: "m", RulesEvaluated: 42);

        var test = Assert.Single(BpaRunRenderer.ToTrxTests(result));
        Assert.Equal(TrxWriter.TrxOutcome.Passed, test.Outcome);
        Assert.Contains("42", test.Name);
    }
}
