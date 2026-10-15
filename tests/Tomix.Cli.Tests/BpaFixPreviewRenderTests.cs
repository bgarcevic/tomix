using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Cli.Tests;

/// <summary>
/// The <c>bpa run --fix</c> preview (#208): an expression fix shows its whole before/after DAX as
/// highlighted, indented blocks, while scalar fixes keep their one-line <c>Prop: "a" → "b"</c>
/// form. Redirected output carries no escape codes and the expression lines copy-paste as written.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class BpaFixPreviewRenderTests
{
    private const string Function = "\x1b[38;5;12m"; // functions (bright blue)
    private const string Reference = "\x1b[38;5;6m"; // measure references (cyan)

    private static readonly BpaFixChange Rewrite = new(
        "QUALIFY_COLUMNS", "Measure", "Sales/Total", BpaFixAction.Set, "Expression",
        Before: "SUM([Amount]) + [Margin]",
        After: "SUM('Sales'[Amount]) + [Margin]")
    { ObjectKind = ModelObjectKind.Measure };

    private static readonly BpaFixChange Format = new(
        "FORMAT_RULE", "Measure", "Sales/Total", BpaFixAction.Set, "FormatString", Before: "", After: "#,##0")
    { ObjectKind = ModelObjectKind.Measure };

    [Fact]
    public void ExpressionFix_ShowsFullHighlightedBeforeAndAfter()
    {
        var output = Render([Rewrite], plain: false);

        Assert.Contains(Function + "SUM", output);
        Assert.Contains(Reference + "[Margin]", output);

        var text = StripAnsi(output);
        Assert.Contains("    Expression:", text);
        Assert.Matches(@"(?m)^      Before:\s*\n        SUM\(\[Amount\]\) \+ \[Margin\]$", text);
        Assert.Matches(@"(?m)^      After:\s*\n        SUM\('Sales'\[Amount\]\) \+ \[Margin\]$", text);
        Assert.DoesNotContain("Expression: \"", text);
    }

    [Fact]
    public void ExpressionFix_Redirected_IsPlainAndCopyPasteable()
    {
        var output = Render([Rewrite], plain: true);

        // The exact preview block: no escape codes, ASCII glyphs, expression lines as written.
        var block = Regex.Match(output, @"  Would fix: .*?(?=\nWould fix \d)", RegexOptions.Singleline).Value;
        Assert.Equal(
            "  Would fix: Measure 'Sales/Total' - QUALIFY_COLUMNS\n"
            + "    Expression:\n"
            + "      Before:\n"
            + "        SUM([Amount]) + [Margin]\n"
            + "      After:\n"
            + "        SUM('Sales'[Amount]) + [Margin]\n",
            block);
    }

    [Fact]
    public void LongExpression_IsCappedWithHiddenLineCount()
    {
        var after = string.Join("\n", Enumerable.Range(1, BpaRunView.FixExpressionLineCap + 2).Select(i => $"+ {i}"));
        var change = Rewrite with { Before = null, After = after };

        var text = Render([change], plain: true);

        Assert.Contains("Before: (unset)", text);
        Assert.Contains($"        + {BpaRunView.FixExpressionLineCap}\n", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain($"+ {BpaRunView.FixExpressionLineCap + 1}", text);
        Assert.Contains("... (+2 lines)", text);
    }

    [Fact]
    public void ScalarFix_KeepsOneLineForm()
    {
        var text = StripAnsi(Render([Format], plain: false));

        Assert.Contains("    FormatString: \"\" → \"#,##0\"", text);
        Assert.DoesNotContain("Before:", text);
    }

    private static string Render(IReadOnlyList<BpaFixChange> changes, bool plain)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                if (plain)
                {
                    // What a pipe gets: no ANSI support (Spectre's detection on a redirected
                    // stream), then the CLI's own redirected-output switch. Default enrichers
                    // are off: on GitHub Actions they turn ANSI back on for CI logs.
                    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
                    {
                        Out = new AnsiConsoleOutput(Console.Out),
                        Ansi = AnsiSupport.No,
                        ColorSystem = ColorSystemSupport.NoColors,
                        Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
                    });
                    StdOut.PlainWhenRedirected(AnsiConsole.Console);
                }
                BpaRunRenderer.Render(
                    Result(changes),
                    new BpaRunView.RunOptions(NoMultiline: false, Full: false, Details: false, Errors: false, Warnings: false, Info: false));
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: !plain);
        return captured.Stdout.Replace("\r\n", "\n");
    }

    private static BpaRunResult Result(IReadOnlyList<BpaFixChange> changes)
    {
        var rule = new BpaRule("QUALIFY_COLUMNS", "Qualify columns", "DAX", RuleSeverity.Warning, Scope: ["Measure"], Expression: "true");
        var violation = new BpaViolation(
            "QUALIFY_COLUMNS", "Qualify columns", "DAX", RuleSeverity.Warning,
            ObjectType: "Measure", ObjectName: "[Total]", ObjectPath: "Sales/Total", CanFix: true);

        return new BpaRunResult([BpaResult.ForViolation(rule, violation)], "MyModel", RulesEvaluated: 1)
        {
            Preview = true,
            FixChanges = changes,
            MeasureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Total", "Margin" },
        };
    }

    private static string StripAnsi(string text) => AnsiCodes().Replace(text, "");

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiCodes();
}
