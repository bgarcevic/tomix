using Spectre.Console;
using Tomix.App.Test;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering for <c>tx test</c>: one status line per test, a difference table for failures,
/// a counts summary, plus the TRX (<c>--trx</c>) and CI annotation (<c>--ci</c>) projections.
/// The JSON projection is the <see cref="TestRunResult"/> record itself.
/// </summary>
internal static class TestRunRenderer
{
    private const string BlankCell = "(blank)";

    public static void Render(TestRunResult result, bool quiet)
    {
        if (!quiet)
        {
            AnsiConsole.MarkupLine(Styling.Title($"DAX tests · {result.Database}"));
            AnsiConsole.MarkupLine(Styling.Muted($"{result.Tests.Count} test(s) from {result.Path}"));
            AnsiConsole.WriteLine();
        }

        foreach (var test in result.Tests)
        {
            var passing = IsPassing(test.Outcome);
            if (quiet && passing)
                continue;

            AnsiConsole.MarkupLine(
                "  {0}  {1} {2}",
                OutcomeLabel(test.Outcome),
                Styling.MarkupEscape(test.Name),
                Styling.Muted($"({test.DurationMs} ms)"));

            if (!passing && !string.IsNullOrEmpty(test.Message))
                AnsiConsole.MarkupLine("        {0}", Styling.Muted(Styling.MarkupEscape(test.Message)));

            if (test.Differences is { Count: > 0 } differences)
                RenderDifferences(differences, test.TotalDifferences);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  " + Summary(result));

        if (result.Missing > 0)
            AnsiConsole.MarkupLine(Styling.Guidance("  Run  tx test --update  to record missing snapshots."));
    }

    private static void RenderDifferences(IReadOnlyList<TestDifference> differences, int total)
    {
        var table = Styling.NewTable("Kind", "Row", "Column", "Expected", "Actual");
        foreach (var difference in differences)
            table.AddRow(
                Styling.MarkupEscape(difference.Kind),
                difference.Row?.ToString() ?? "",
                Styling.MarkupEscape(difference.Column ?? ""),
                Cell(difference.Expected),
                Cell(difference.Actual));

        AnsiConsole.Write(table);
        if (total > differences.Count)
            AnsiConsole.MarkupLine(Styling.Muted($"        … and {total - differences.Count} more difference(s)."));
    }

    private static string Cell(string? value)
        => value is null ? Styling.Muted(BlankCell) : Styling.MarkupEscape(value);

    private static string Summary(TestRunResult result)
    {
        var unchanged = result.Tests.Count(t => t.Outcome == TestOutcome.Unchanged);
        var parts = new List<string>(6);
        if (result.Passed > 0) parts.Add(Styling.Success($"{result.Passed} passed"));
        if (result.Failed > 0) parts.Add(Styling.Error($"{result.Failed} failed"));
        if (result.Missing > 0) parts.Add(Styling.Warning($"{result.Missing} missing"));
        if (result.Errored > 0) parts.Add(Styling.Error($"{result.Errored} errored"));
        if (result.Updated > 0) parts.Add(Styling.Success($"{result.Updated} updated"));
        if (unchanged > 0) parts.Add(Styling.Muted($"{unchanged} unchanged"));
        if (parts.Count == 0) parts.Add(Styling.Muted("no tests"));

        return string.Join(" · ", parts)
            + Styling.Muted($"  ({Styling.DurationSeconds(result.DurationMs / 1000.0)})");
    }

    private static bool IsPassing(TestOutcome outcome)
        => outcome is TestOutcome.Passed or TestOutcome.Updated or TestOutcome.Unchanged;

    private static string OutcomeLabel(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => Styling.Success("PASS"),
        TestOutcome.Failed => Styling.Error("FAIL"),
        TestOutcome.Missing => Styling.Warning("MISS"),
        TestOutcome.Error => Styling.Error("ERROR"),
        TestOutcome.Updated => Styling.Success("UPDATED"),
        TestOutcome.Unchanged => Styling.Muted("UNCHANGED"),
        _ => Styling.Muted(outcome.ToString().ToUpperInvariant())
    };

    /// <summary>
    /// TRX projection: one test per case. Passed/Updated/Unchanged map to Passed,
    /// Failed/Missing to Failed (a test without its snapshot must fail the run), Error to Error.
    /// </summary>
    public static IReadOnlyList<TrxWriter.TrxTest> ToTrxTests(TestRunResult result)
        => result.Tests
            .Select(test => new TrxWriter.TrxTest(
                test.Name,
                test.Outcome switch
                {
                    TestOutcome.Failed or TestOutcome.Missing => TrxWriter.TrxOutcome.Failed,
                    TestOutcome.Error => TrxWriter.TrxOutcome.Error,
                    _ => TrxWriter.TrxOutcome.Passed
                },
                TrxMessage(test)))
            .ToList();

    private static string? TrxMessage(TestCaseResult test)
    {
        if (IsPassing(test.Outcome))
            return null;

        var lines = new List<string>(1 + (test.Differences?.Count ?? 0));
        if (!string.IsNullOrEmpty(test.Message))
            lines.Add(test.Message);

        foreach (var difference in test.Differences ?? [])
            lines.Add(DescribeDifference(difference));

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    public static void EmitCi(string? ci, TestRunResult result)
    {
        var annotations = result.Tests
            .Where(test => !IsPassing(test.Outcome))
            .Select(test =>
            {
                var summary = test.Differences is { Count: > 0 } differences
                    ? $"{test.Message} First: {DescribeDifference(differences[0])}"
                    : test.Message ?? test.Outcome.ToString();
                return new CiAnnotation(IsError: true, $"tx test {test.Name}: {summary}");
            })
            .ToList();

        CiAnnotations.Emit(ci, annotations, Console.Error);
    }

    private static string DescribeDifference(TestDifference difference)
    {
        var location = difference.Kind switch
        {
            "cell" => $"row {difference.Row}, {difference.Column}",
            "column" => difference.Column is null ? "columns" : $"column {difference.Column}",
            _ => "row count"
        };
        return $"{location}: expected {Describe(difference.Expected)}, actual {Describe(difference.Actual)}";
    }

    private static string Describe(string? value)
        => value is null ? BlankCell : $"'{value}'";
}
