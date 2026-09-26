using System.Text.RegularExpressions;
using Tomix.App.Bpa;
using Tomix.App.Refresh;
using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

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
        { "bpa", "BPA analysis · basic-tmdl", "Rules evaluated:" },
        { "refresh", "Refreshed Prod on", "Sales" },
    };

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

    private static void Render(string command)
    {
        switch (command)
        {
            case "validate":
                var issue = new ValidationIssue(ValidationSeverity.Error, "TOMIX_X", "Broken", "Sales/Total", Expression: null);
                ValidateRenderer.Render(
                    new ValidateModelResult("basic-tmdl", Valid: false, DurationMs: 1, Errors: [issue], Warnings: []),
                    errorsOnly: false, noMultiline: true, includeBanner: true);
                break;
            case "bpa":
                var violation = new BpaViolation(
                    "R1", "Rule", "Category", BpaSeverity.Warning, "Column", "Amount", "Sales/Amount", "Why");
                BpaRunRenderer.Render(
                    new BpaRunResult(
                        [new BpaResult(BpaResultKind.Violation, "R1", "Rule", "Category", BpaSeverity.Warning, Violation: violation)],
                        "basic-tmdl", RulesEvaluated: 1),
                    new BpaRunView.RunOptions(false, false, false, false, false, false));
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
