using Spectre.Console;
using Tomix.App.Test;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering for <c>tx test list</c> (one line per test) and <c>tx test init</c> (the files
/// written and what to run next). The JSON projections are the result records themselves.
/// </summary>
internal static class TestScaffoldRenderer
{
    public static void RenderList(TestListResult result, bool quiet)
    {
        if (result.Tests.Count == 0)
        {
            StdErr.MarkupLine(Styling.Muted($"No matching .dax tests under {result.Path}."));
            return;
        }

        var nameWidth = result.Tests.Max(test => test.Name.Length);
        foreach (var test in result.Tests)
        {
            var name = Styling.MarkupEscape(test.Name.PadRight(nameWidth));
            if (test.Error is not null)
            {
                AnsiConsole.MarkupLine("  {0}  {1}  {2}", name, Styling.Error("invalid"), Styling.Muted(test.Error));
                continue;
            }

            var checks = test.Checks!;
            var snapshot = checks is "snapshot" or "assertions+snapshot" && !test.HasSnapshot
                ? "  " + Styling.Warning("no snapshot yet")
                : "";
            var tags = test.Tags.Count > 0 ? "  " + Styling.Muted(string.Join(", ", test.Tags.Select(tag => "#" + tag))) : "";
            AnsiConsole.MarkupLine("  {0}  {1}{2}{3}", name, Styling.Value(checks.PadRight(19)), tags, snapshot);
        }

        if (!quiet)
            StdErr.MarkupLine(Styling.Muted($"{result.Tests.Count} test(s) under {result.Path}"));
    }

    public static void RenderInit(TestInitResult result, bool quiet)
    {
        foreach (var file in result.Created)
            AnsiConsole.MarkupLine(Styling.Success($"  created  {file}"));
        foreach (var file in result.Skipped)
            AnsiConsole.MarkupLine(Styling.Muted($"  exists   {file}"));

        if (quiet)
            return;

        var err = StdErr.Console();
        err.WriteLine();
        err.MarkupLine(Styling.Guidance("Next (add -s <workspace> -d <model> unless a model is connected):"));
        foreach (var step in result.NextSteps)
            err.MarkupLine(Styling.Guidance($"  {step}"));
    }
}
