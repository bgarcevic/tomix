using Tomix.App.Deploy;
using Tomix.App.Query;
using Tomix.App.Test;
using Tomix.App.Validate;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pin: command banners name the model through <c>ModelDisplayName</c> resolution, so a model
/// its TMDL (or folder, or <c>.platform</c>) names never shows up as "(unnamed)". Covers the
/// validate/deploy/test/query banners; the query renderer prints no model name at all, which
/// the last assertion locks in.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ModelBannerNameTests
{
    private const string Named = "basic-tmdl";

    [Fact]
    public void ValidateBanner_ShowsResolvedModelName()
    {
        var result = new ValidateModelResult(
            ModelName: Named, Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains($"Validating: {Named}", captured.Stderr);
        Assert.DoesNotContain("(unnamed)", captured.Stderr);
    }

    [Fact]
    public void DeployBanner_ResolvesDisplayNameFromSourcePath()
    {
        // A plain folder model (no TOM name, no .platform) resolves through the folder branch.
        var source = Path.Combine(Path.GetTempPath(), Named);
        var result = new DeployModelResult(
            Server: "powerbi://ws", Database: "db", Status: "created", DurationMs: 5,
            ScriptPath: null, Script: null);

        var captured = ConsoleCapture.Run(
            () => DeployRenderer.Render(result, source),
            captureAnsiConsole: true);

        Assert.Contains($"Deploying {Named} to", captured.Stdout);
        Assert.DoesNotContain("(unnamed)", captured.Stdout);
    }

    [Fact]
    public void DeployBanner_DryRun_UsesResolvedDisplayName()
    {
        var source = Path.Combine(Path.GetTempPath(), Named);
        var result = new DeployModelResult(
            Server: "powerbi://ws", Database: "db", Status: "dry-run", DurationMs: 5,
            ScriptPath: null, Script: null);

        var captured = ConsoleCapture.Run(
            () => DeployRenderer.Render(result, source),
            captureAnsiConsole: true);

        Assert.Contains($"Dry run: {Named} to", captured.Stdout);
        Assert.DoesNotContain("(unnamed)", captured.Stdout);
    }

    [Fact]
    public void TestBanner_ShowsDatabaseName_NeverUnnamed()
    {
        var result = new TestRunResult(
            Server: "powerbi://ws", Database: Named, Path: "./tests",
            Tests: [new TestCaseResult("totals", "/tests/totals.dax", TestOutcome.Passed, 1, null, null, 0)],
            Passed: 1, Failed: 0, Missing: 0, Errored: 0, Updated: 0, DurationMs: 5);

        var captured = ConsoleCapture.Run(
            () => TestRunRenderer.Render(result, quiet: false),
            captureAnsiConsole: true);

        Assert.Contains($"DAX tests · {Named}", captured.Stderr);
        Assert.DoesNotContain("(unnamed)", captured.Stderr);
    }

    [Fact]
    public void QueryRenderer_PrintsNoModelName()
    {
        var result = new QueryModelResult(
            Server: "powerbi://ws", Database: Named, Columns: [], Rows: [], RowCount: 0,
            Truncated: false, DurationMs: 1);

        var captured = ConsoleCapture.Run(
            () => QueryResultRenderer.Render(result, quiet: false),
            captureAnsiConsole: true);

        Assert.DoesNotContain("(unnamed)", captured.Stdout);
    }
}
