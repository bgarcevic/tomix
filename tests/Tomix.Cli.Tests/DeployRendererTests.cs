using System.Text.RegularExpressions;

using Tomix.App.Deploy;
using Tomix.App.Diff;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The dry-run branch of the deploy renderer — the user-facing half of the #128 behavior
/// ("preview what the deploy would change"). Each of its four outcomes (first deploy,
/// identical plan, change list, failed diff) prints a different message, so every branch is
/// pinned. Asserted on captured plain text; styling is not under test here.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DeployRendererTests
{
    private const string Server = "powerbi://api.powerbi.com/v1.0/myorg/W";

    [Fact]
    public void MissingTarget_SaysTheDeployCreatesIt()
    {
        var output = Render(DryRun(createsDatabase: true));

        Assert.Contains("Target database does not exist", output);
        Assert.Contains("creates it with the full source model", output);
    }

    [Fact]
    public void IdenticalPlan_SaysNoChanges()
    {
        var output = Render(DryRun(diff: NoChanges()));

        Assert.Contains("No changes — local and remote are identical.", output);
    }

    [Fact]
    public void ChangedPlan_ShowsSummaryAndChangeLines()
    {
        var output = Render(DryRun(diff: new DiffModelResult(
            HasChanges: true,
            Summary: new DiffSummary(Added: 1, Removed: 1, Modified: 1),
            Changes:
            [
                new DiffChange("added", "Measure", "Sales/Total Sales"),
                new DiffChange("removed", "Table", "Sales"),
                new DiffChange("modified", "Partition", "Sales/Fact", "2024", "Fact")
            ])));

        Assert.Contains("1 added, 1 removed, 1 modified", output);
        Assert.Contains("+ Measure Sales/Total Sales", output);
        Assert.Contains("- Table Sales", output);
        Assert.Contains("~ Partition Sales/Fact", output);
        Assert.Contains("- 2024", output);
        Assert.Contains("+ Fact", output);
    }

    [Fact]
    public void DiffFailure_SaysDiffUnavailableButPlanShown()
    {
        var output = Render(DryRun(diffError: "not authenticated"));

        Assert.Contains("Diff unavailable: not authenticated", output);
        Assert.Contains("Showing deploy plan only.", output);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static DeployModelResult DryRun(
        DiffModelResult? diff = null,
        string? diffError = null,
        bool? createsDatabase = null)
        => new(Server, "Prod", Status: "dry-run", DurationMs: 0, ScriptPath: null, Script: null)
        {
            Diff = diff,
            DiffError = diffError,
            CreatesDatabase = createsDatabase
        };

    private static DiffModelResult NoChanges()
        => new(HasChanges: false, Summary: new DiffSummary(0, 0, 0), Changes: []);

    private static string Render(DeployModelResult result)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                DeployRenderer.Render(result, "samples/basic-tmdl");
                return 0;
            },
            captureAnsiConsole: true);
        Assert.Equal(0, captured.ExitCode);
        // Spectre emits true-color escapes even in detect mode under redirection and soft-wraps
        // long lines at the detected console width; neither styling nor wrapping is under test
        // here, so strip escapes and collapse whitespace before asserting.
        return CollapseSpace(AnsiEscapes.Replace(captured.Stdout, ""));
    }

    private static readonly Regex AnsiEscapes = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    private static string CollapseSpace(string text)
        => Regex.Replace(text, @"\s+", " ");
}
