using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;

namespace Tomix.Cli.Tests;

/// <summary>
/// Redirected <c>tx bpa run</c> output is pure ASCII: severity bullets, counts, separators, the
/// pass/fail mark and the rule line all switch with <see cref="StdOut.PlainWhenRedirected"/>, so a
/// pipe through an OEM-code-page consumer (Windows PowerShell 5.1, <c>more</c>) stays readable.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class BpaRedirectedOutputTests
{
    [Fact]
    public void RedirectedRun_WritesOnlyAscii()
    {
        var output = Render(plain: true);

        Assert.Contains("* ERROR", output);
        Assert.Matches(@"(?m)^  x1  Avoid floats", output);
        Assert.Matches(@"(?m)^FAIL \d+ errors? ", output);
        Assert.DoesNotMatch(@"[^\x00-\x7F]", output);
    }

    [Fact]
    public void TerminalRun_KeepsUnicodeSymbols()
    {
        var output = Render(plain: false);

        Assert.Contains("● ERROR", output);
        Assert.Contains("×1", output);
    }

    [Fact]
    public void AsciiGlyphs_ReachTheViewLines()
    {
        Assert.Equal(
            "3 errors - 32 warnings in 5 of 27 rules - 22 passed - 326ms",
            BpaRunView.SummaryLine(3, 32, 0, 5, 27, 326, glyphs: Glyphs.Ascii));
        Assert.Equal("... +2 more", BpaRunView.ObjectLines(["a", "b", "c"], full: false, cap: 1, Glyphs.Ascii)[^1]);
    }

    private static string Render(bool plain)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                if (plain)
                    StdOut.PlainWhenRedirected(AnsiConsole.Console);
                BpaRunRenderer.Render(
                    Result(),
                    new BpaRunView.RunOptions(NoMultiline: false, Full: false, Details: true, Errors: false, Warnings: false, Info: false));
            },
            captureAnsiConsole: true);
        return AnsiCodes.Replace(captured.Stdout, "");
    }

    private static readonly Regex AnsiCodes = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    private static BpaRunResult Result()
    {
        var rule = new BpaRule("AVOID_FLOATS", "[Performance] Avoid floats", "Performance", RuleSeverity.Error, Scope: ["Column"], Expression: "true");
        var violation = new BpaViolation(
            RuleId: "AVOID_FLOATS",
            RuleName: "[Performance] Avoid floats",
            Category: "Performance",
            Severity: RuleSeverity.Error,
            ObjectType: "Column",
            ObjectName: "Sales[Amount]",
            ObjectPath: "Sales/Amount",
            Description: "Do not use floating point.",
            CanFix: true);

        return new BpaRunResult(
            Results:
            [
                BpaResult.ForViolation(rule, violation),
                BpaResult.Sentinel(BpaResultKind.CompilationError, rule with { Id = "BROKEN_RULE" }, "boom", "model")
            ],
            ModelName: "MyModel",
            RulesEvaluated: 4,
            DurationMs: 12,
            FixesApplied: 1,
            FixesSkipped: 2,
            DestructiveFixesSkipped: 3,
            FixErrors: ["fix failed"],
            RuleLoadDiagnostics: [])
        { FixOutcome = new MutationOutcome(MutationStatus.Saved, "C:/model", PersistenceKind.File, SyncOutcome.NotConfigured) };
    }
}
