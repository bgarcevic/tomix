using System.Text.RegularExpressions;
using Tomix.App.Bpa;
using Tomix.App.Deploy;
using Tomix.App.Info;
using Tomix.App.Script;
using Tomix.App.Test;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Update;

namespace Tomix.Cli.Tests;

/// <summary>Renderer-specific checks for paths that compose model text with styled output.</summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class MarkupEscapeRendererTests
{
    [Fact]
    public void Connect_UsesLiteralServerAndDatabaseNames()
    {
        var captured = Capture(() => ConnectRenderer.RenderConnectedModelText(
            new InfoModelResult(new ModelSummary("[Model]", 1600, 1, 1, 1, 0, 0)),
            model: null, remoteServer: "[Server]", database: "[Database]", workspace: null));

        AssertLiteral(captured.Stdout, "Model: [Model]", "Active: [Server] / [Database]");
    }

    [Fact]
    public void BpaRules_UsesLiteralRuleAndSyncNames()
    {
        var captured = Capture(() =>
        {
            BpaRulesRenderer.RenderDisable(new BpaRulesDisableResult("[Rule]", true, false, []));
            BpaRulesRenderer.RenderIgnore(new BpaRulesIgnoreResult(
                "[Rule]", true, true, ["[Rule]"], false, null, "[Model]",
                Synced: true, SyncTarget: "[Target]"));
        });

        AssertLiteral(captured.Stdout,
            "Rule '[Rule]' was already disabled",
            "Rule [Rule] is now ignored for [Model].",
            "Synced: [Target]");
    }

    [Fact]
    public void BpaRun_UsesLiteralViolationAndSyncNames()
    {
        var violation = new BpaViolation(
            "[Rule]", "[Rule]", "[Category]", BpaSeverity.Error,
            "Column", "[Column]", "[Table]/[Column]", "[Description]", CanFix: true);
        var result = new BpaRunResult(
            [new BpaResult(BpaResultKind.Violation, "[Rule]", "[Rule]", "[Category]",
                BpaSeverity.Error, Violation: violation)],
            "[Model]", RulesEvaluated: 1, FixesApplied: 1,
            Synced: true, SyncTarget: "[Target]");
        var options = new BpaRunView.RunOptions(false, true, true, false, false, false);

        var captured = Capture(() => BpaRunRenderer.Render(result, options));

        AssertLiteral(captured.Stdout,
            "BPA analysis · [Model]", "[Category]", "[Column]", "Synced: [Target]");
    }

    [Fact]
    public void Script_UsesLiteralModelAndSyncNames()
    {
        var result = ScriptRunResult.Executed(
            "[Model]", 1, [], [], saved: true, synced: true, syncTarget: "[Target]");

        var captured = Capture(() => ScriptRenderer.RenderText(result, "text"));

        AssertLiteral(captured.Stdout, "Model: [Model]", "Synced: [Target]");
    }

    [Fact]
    public void TestRun_UsesLiteralTestAndFailureText()
    {
        var result = new TestRunResult(
            "[Server]", "[Database]", "[Path]",
            [new TestCaseResult("[Test]", "", TestOutcome.Failed, 1, Message: "[Failure]")],
            Passed: 0, Failed: 1, Missing: 0, Errored: 0, Updated: 0, DurationMs: 1);

        var captured = Capture(() => TestRunRenderer.Render(result, quiet: true));

        AssertLiteral(captured.Stdout, "[Test]", "[Failure]");
    }

    [Fact]
    public void Update_UsesLiteralReleaseNotesAndBreakingBadge()
    {
        var result = new UpdateCheckResult(
            "[current]", "[latest]", true, InstallKind.Development,
            [new ReleaseSummary("[release]", null, true, "[note]")]);

        var captured = Capture(() => UpdateRenderer.RenderCheck(result));

        AssertLiteral(captured.Stdout, "Installed:  [current]", "v[release] [breaking]", "[note]");
    }

    [Fact]
    public void Deploy_UsesLiteralDiffError()
    {
        var result = new DeployModelResult("[Server]", "[Database]", "dry-run", null, null, null,
            DiffError: "[Diff error]");

        var captured = Capture(() => DeployRenderer.Render(result, SampleModel.Locate()));

        AssertLiteral(captured.Stdout, "Diff unavailable: [Diff error]");
    }

    [Fact]
    public void DidYouMean_UsesLiteralSuggestion()
    {
        var captured = Capture(() => DidYouMean.WriteSuggestion("[Rul]", ["[Rule]"]));

        AssertLiteral(captured.Stderr, "Did you mean '[Rule]'?");
    }

    private static ConsoleCapture.Captured Capture(Action render)
        => ConsoleCapture.Run(() => { render(); return 0; }, captureAnsiConsole: true, forceAnsi: true);

    private static void AssertLiteral(string output, params string[] expected)
    {
        var visible = AnsiRegex().Replace(output, "");
        foreach (var value in expected)
        {
            Assert.Contains(value, visible);
            foreach (Match bracketed in BracketRegex().Matches(value))
                Assert.DoesNotContain($"[{bracketed.Value}]", visible);
        }
    }

    [GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex("\\[[^\\[\\]]+\\]")]
    private static partial Regex BracketRegex();
}
