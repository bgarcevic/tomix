using System.Text.RegularExpressions;
using Tomix.App.Bpa;
using Tomix.App.Refresh;
using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Cli.Tests;

/// <summary>
/// Stdout is the result and stderr the commentary (issue #255): <c>tx validate|bpa run|refresh
/// &gt; file</c> must leave only results in the file. Each case names a banner that belongs on
/// stderr and a result line that belongs on stdout.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class StdoutStderrSplitTests
{
    public static TheoryData<string, string, string> Cases => new()
    {
        { "validate", "Validating: basic-tmdl", "Errors:" },
        { "bpa", "BPA analysis · basic-tmdl", "1 warning in 1 of 1 rule" },
        { "bpa", "tx bpa run --details", "R1 · Category" },
        { "refresh", "Refreshed Prod on", "Sales" },
        { "bpa rules list", "tx bpa rules show R1", "R1 · warning · Column · fixable" },
    };

    [Fact]
    public void RedirectedStderr_KeepsLongLinesWhole()
    {
        // Redirected stderr has no width; Spectre's 80-column fallback used to hard-wrap, splitting
        // a long model path in the "Connected to:" banner across lines.
        var path = Path.Combine("C:", "Users", "someone with a long name", "repos", "tomix-cli",
            ".claude", "worktrees", "a-fairly-long-worktree-name", "samples", "basic-tmdl");
        var line = $"Connected to: {path}";

        var captured = ConsoleCapture.Run(() => StdErr.MarkupLine(Styling.Muted(line)));

        Assert.True(line.Length > 80, "The line must be longer than Spectre's fallback width.");
        // Strip color only (CI runners get it; a local run may not). Line breaks must survive, or
        // a wrapped line would pass.
        Assert.Equal(line, Ansi().Replace(captured.Stderr, "").TrimEnd());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Banner_GoesToStderr_ResultStaysOnStdout(string command, string banner, string resultLine)
    {
        var captured = ConsoleCapture.Run(
            () => { Render(command); return 0; },
            captureAnsiConsole: true);

        var stdout = Plain(captured.Stdout);
        var stderr = Plain(captured.Stderr);
        Assert.Contains(banner, stderr);
        Assert.DoesNotContain(banner, stdout);
        Assert.Contains(resultLine, stdout);
    }

    [Theory]
    // #266: `tx vertipaq` needs a live engine, so a plain model file gets the workspace-mode route.
    [InlineData(true, "Collect them: tx vertipaq --annotate --save")]
    [InlineData(false, "Collect them from a deployed copy: tx connect <path> -w <workspace> <model> tx vertipaq --annotate --save")]
    public void MissingVertipaqStats_NamesRulesOnStdout_AndHintFitsTheModel(bool canCollect, string hint)
    {
        var rule = new BpaRule("STATS_RULE", "Stats rule", "Performance", RuleSeverity.Warning, ["Table"]);
        var captured = ConsoleCapture.Run(
            () =>
            {
                BpaRunRenderer.Render(
                    new BpaRunResult(
                        [BpaResult.Sentinel(BpaResultKind.MissingVertipaqStats, rule, BpaEngine.MissingVertipaqStatsMessage)],
                        "basic-tmdl", RulesEvaluated: 1),
                    new BpaRunView.RunOptions(false, false, false, false, false, false,
                        CanCollectVertipaqStats: canCollect));
                return 0;
            },
            captureAnsiConsole: true);

        var stdout = Plain(captured.Stdout);
        Assert.Contains("0 of 1 rule passed · 1 not checked", stdout);
        Assert.Contains("Not checked (1): the model has no VertiPaq statistics STATS_RULE", stdout);
        Assert.Contains(hint, Plain(captured.Stderr));
    }

    private static void Render(string command)
    {
        switch (command)
        {
            case "validate":
                var issue = new ValidationIssue(RuleSeverity.Error, "TOMIX_X", "Broken", "Sales/Total", Expression: null);
                ValidateRenderer.Render(
                    new ValidateModelResult("basic-tmdl", Valid: false, DurationMs: 1, Errors: [issue], Warnings: []),
                    errorsOnly: false, noMultiline: true, includeBanner: true);
                break;
            case "bpa":
                var violation = new BpaViolation(
                    "R1", "Rule", "Category", RuleSeverity.Warning, "Column", "Amount", "Sales/Amount", "Why");
                BpaRunRenderer.Render(
                    new BpaRunResult(
                        [new BpaResult(BpaResultKind.Violation, "R1", "Rule", "Category", RuleSeverity.Warning, Violation: violation)],
                        "basic-tmdl", RulesEvaluated: 1),
                    new BpaRunView.RunOptions(false, false, false, false, false, false));
                break;
            case "bpa rules list":
                BpaRulesRenderer.RenderList(new BpaRulesListResult(
                    [new BpaRuleInfo("standard", "active", "R1", "[Category] Rule", "Category", RuleSeverity.Warning,
                        "Column", "Why", "true", "IsHidden = true", Enabled: true)],
                    new BpaRulesSummary(Total: 1, Active: 1, Disabled: 0, Ignored: 0)));
                break;
            case "refresh":
                RefreshRenderer.Render(new RefreshModelResult(
                    "powerbi://api.powerbi.com/v1.0/myorg/ws", "Prod", "full", DurationMs: 1000,
                    Tables: [new RefreshTableResult("Sales", 100, 5, 5, 10)],
                    Totals: new RefreshTableResult("Total", 100, 5, 5, 10),
                    Script: null));
                break;
        }
    }

    private static string Plain(string text) => Whitespace().Replace(Ansi().Replace(text, ""), " ");

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
