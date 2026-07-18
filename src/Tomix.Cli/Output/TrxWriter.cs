using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Tomix.Cli.Output;

/// <summary>
/// Writes VSTEST <c>.trx</c> files (`validate --trx`, `bpa run --trx`) with one test per
/// projected <see cref="TrxTest"/>, in the shape Azure DevOps' test-run ingestion expects
/// (TestDefinitions/Results/TestEntries/TestLists/Counters). Test and execution ids are
/// derived from the run name and test index so output is deterministic for a given result.
/// </summary>
internal static class TrxWriter
{
    public enum TrxOutcome
    {
        Passed,
        Failed,
        Warning,
        Error
    }

    public sealed record TrxTest(string Name, TrxOutcome Outcome, string? Message = null);

    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // Well-known VSTEST constants: the unit-test type id and the default "Results Not in a List" list.
    private const string UnitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b";
    private const string DefaultTestListId = "8c84fa94-04c1-424b-9868-57a2d4851a1d";

    public static void Write(string path, string runName, IReadOnlyList<TrxTest> tests)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var now = DateTimeOffset.UtcNow.ToString("o");
        var computer = Environment.MachineName;

        var results = new XElement(Ns + "Results");
        var definitions = new XElement(Ns + "TestDefinitions");
        var entries = new XElement(Ns + "TestEntries");

        for (var i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            var testId = StableGuid($"{runName}|test|{i}|{test.Name}");
            var executionId = StableGuid($"{runName}|execution|{i}|{test.Name}");

            var result = new XElement(Ns + "UnitTestResult",
                new XAttribute("executionId", executionId),
                new XAttribute("testId", testId),
                new XAttribute("testName", test.Name),
                new XAttribute("computerName", computer),
                new XAttribute("testType", UnitTestType),
                new XAttribute("testListId", DefaultTestListId),
                new XAttribute("outcome", test.Outcome.ToString()));

            if (!string.IsNullOrEmpty(test.Message))
                result.Add(new XElement(Ns + "Output",
                    new XElement(Ns + "ErrorInfo",
                        new XElement(Ns + "Message", test.Message))));

            results.Add(result);

            definitions.Add(new XElement(Ns + "UnitTest",
                new XAttribute("id", testId),
                new XAttribute("name", test.Name),
                new XElement(Ns + "Execution", new XAttribute("id", executionId)),
                new XElement(Ns + "TestMethod",
                    new XAttribute("codeBase", "tx"),
                    new XAttribute("className", runName),
                    new XAttribute("name", test.Name))));

            entries.Add(new XElement(Ns + "TestEntry",
                new XAttribute("testId", testId),
                new XAttribute("executionId", executionId),
                new XAttribute("testListId", DefaultTestListId)));
        }

        var failed = tests.Count(t => t.Outcome == TrxOutcome.Failed);
        var error = tests.Count(t => t.Outcome == TrxOutcome.Error);

        var doc = new XDocument(
            new XElement(Ns + "TestRun",
                new XAttribute("id", StableGuid($"{runName}|run")),
                new XAttribute("name", runName),
                new XElement(Ns + "Times",
                    new XAttribute("creation", now),
                    new XAttribute("queuing", now),
                    new XAttribute("start", now),
                    new XAttribute("finish", now)),
                results,
                definitions,
                entries,
                new XElement(Ns + "TestLists",
                    new XElement(Ns + "TestList",
                        new XAttribute("name", "Results Not in a List"),
                        new XAttribute("id", DefaultTestListId))),
                new XElement(Ns + "ResultSummary",
                    new XAttribute("outcome", failed + error > 0 ? "Failed" : "Completed"),
                    new XElement(Ns + "Counters",
                        new XAttribute("total", tests.Count),
                        new XAttribute("executed", tests.Count),
                        new XAttribute("passed", tests.Count(t => t.Outcome == TrxOutcome.Passed)),
                        new XAttribute("failed", failed),
                        new XAttribute("error", error),
                        new XAttribute("warning", tests.Count(t => t.Outcome == TrxOutcome.Warning))))));

        doc.Save(fullPath);
    }

    private static Guid StableGuid(string seed)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
}
