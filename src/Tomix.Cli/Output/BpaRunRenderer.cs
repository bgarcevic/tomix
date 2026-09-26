using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Output;

/// <summary>
/// Spectre rendering for <c>bpa run</c>: violations grouped by severity (compact or
/// per-rule detail), diagnostics footer, JSON projection, and CI logging commands.
/// Layout decisions live in <see cref="BpaRunView"/>; this file only formats and prints.
/// </summary>
internal static class BpaRunRenderer
{
    private const int MaxTextWidth = 84;

    public static void Render(BpaRunResult result, BpaRunView.RunOptions view)
    {
        // The title is commentary: stderr keeps `tx bpa run > file` down to the findings.
        StdErr.MarkupLine(Styling.Title($"BPA analysis · {result.ModelName}"));

        var groups = BpaRunView.OrderRuleGroups(result.Violations);
        var visible = groups
            .Where(g => BpaRunView.MatchesFilter(g.Severity, view.Errors, view.Warnings, view.Info))
            .ToList();
        var fixRan = result.FixesApplied > 0 || result.FixesSkipped > 0
            || result.DestructiveFixesSkipped > 0 || result.FixErrors is { Count: > 0 };

        AnsiConsole.WriteLine();
        if (groups.Count > 0)
        {
            if (result.FixesApplied > 0)
                AnsiConsole.MarkupLine(Styling.Muted("Findings before fix:"));

            if (visible.Count == 0)
                AnsiConsole.MarkupLine(Styling.Muted("Nothing to show for the selected severities."));
            else
                RenderSections(visible, view);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule().RuleStyle(new Style(Palette.Slate)));
        }

        RenderSummary(result, groups.Count, view);
        RenderDiagnostics(result, view);

        if (fixRan)
            RenderFixOutcome(result, view);

        // After --fix the listed findings are pre-fix, so browsing hints would mislead.
        if (visible.Count > 0 && !fixRan)
            RenderHints(result, visible, view);
    }

    private static void RenderSummary(BpaRunResult result, int failedRules, BpaRunView.RunOptions view)
    {
        var errors = result.Violations.Count(v => v.Severity == BpaSeverity.Error);
        var warnings = result.Violations.Count(v => v.Severity == BpaSeverity.Warning);
        var info = result.Violations.Count(v => v.Severity == BpaSeverity.Info);

        var text = BpaRunView.SummaryLine(errors, warnings, info, failedRules, result.RulesEvaluated, result.DurationMs);
        var line = failedRules == 0 ? Styling.Success($"✓ {text}")
            : errors > 0 ? Styling.Error($"✗ {text}")
            : warnings > 0 ? Styling.Warning($"✗ {text}")
            : $"✗ {Styling.MarkupEscape(text)}";

        if (view.Errors || view.Warnings || view.Info)
        {
            var shown = new List<string>(3);
            if (view.Errors) shown.Add("errors");
            if (view.Warnings) shown.Add("warnings");
            if (view.Info) shown.Add("info");
            line += "  " + Styling.Muted($"(showing {string.Join(" + ", shown)})");
        }

        AnsiConsole.MarkupLine(line);
    }

    /// <summary>
    /// One section per severity. Each rule is a count, its name, and a muted metadata line
    /// (rule ID · category · fixable) packed so the ID is never split; <c>--details</c> adds
    /// the guidance and the affected objects under the same indent.
    /// </summary>
    private static void RenderSections(IReadOnlyList<BpaRunView.RuleGroup> groups, BpaRunView.RunOptions view)
    {
        var countWidth = groups.Max(g => $"×{g.Objects.Count}".Length);
        var indent = new string(' ', 2 + countWidth + 2);
        // Wrap to the effective render width so Spectre never re-wraps (which would split words).
        var width = Math.Max(24, Math.Min(MaxTextWidth, AnsiConsole.Profile.Width) - indent.Length);

        var first = true;
        foreach (var section in BpaRunView.SeveritySections(groups))
        {
            if (!first)
                AnsiConsole.WriteLine();
            first = false;

            var rules = section.Groups.Count == 1 ? "1 rule" : $"{section.Groups.Count} rules";
            var objects = section.ObjectCount == 1 ? "1 object" : $"{section.ObjectCount} objects";
            AnsiConsole.MarkupLine(
                $"{Styling.SeverityHeading(BpaRunView.SeverityWord(section.Severity))}  {Styling.Muted($"{rules} · {objects}")}");

            foreach (var group in section.Groups)
            {
                if (view.Details)
                    AnsiConsole.WriteLine();
                RenderRule(group, view, countWidth, indent, width);
            }
        }
    }

    private static void RenderRule(
        BpaRunView.RuleGroup group, BpaRunView.RunOptions view, int countWidth, string indent, int width)
    {
        var count = $"×{group.Objects.Count}".PadRight(countWidth);
        var name = BpaRunView.StripCategoryPrefix(group.RuleName, group.Category);
        var nameLines = BpaRunView.WrapText(name, width);
        for (var i = 0; i < nameLines.Count; i++)
        {
            var lead = i == 0 ? $"  {Styling.Muted(count)}  " : indent;
            AnsiConsole.MarkupLine(lead + Styling.Bold(nameLines[i]));
        }

        var fixable = BpaRunView.FixableLabel(group);
        foreach (var line in BpaRunView.PackSegments([group.RuleId, group.Category, fixable], width))
        {
            var parts = line.Select(s => ReferenceEquals(s, fixable) ? Styling.Success(s) : Styling.Muted(s));
            AnsiConsole.MarkupLine(indent + string.Join(Styling.Muted(" · "), parts));
        }

        if (!view.Details)
            return;

        var guidance = BpaRunView.Guidance(group.Description, view.NoMultiline);
        foreach (var line in BpaRunView.WrapText(guidance, width))
            AnsiConsole.MarkupLine(indent + Styling.Guidance(line));

        var objects = BpaRunView.ObjectLines(group.Objects, view.Full);
        if (objects.Count == 0)
            return;

        AnsiConsole.MarkupLine(indent + Styling.Muted("Affects:"));
        foreach (var obj in objects)
            AnsiConsole.MarkupLine($"{indent}  {Styling.MarkupEscape(obj)}");
    }

    /// <summary>
    /// One block for what <c>--fix</c> did: how many findings were fixed and remain, anything
    /// held back, and where the result went (saved, staged, or only in memory).
    /// </summary>
    private static void RenderFixOutcome(BpaRunResult result, BpaRunView.RunOptions view)
    {
        AnsiConsole.WriteLine();

        var total = result.Violations.Count;
        var parts = new List<string>
        {
            $"Fixed {result.FixesApplied} of {total} findings",
            $"{Math.Max(0, total - result.FixesApplied)} remain"
        };
        if (result.FixesSkipped > 0)
            parts.Add($"{result.FixesSkipped} skipped");
        var summary = string.Join(" · ", parts);
        AnsiConsole.MarkupLine(result.FixesApplied > 0 ? Styling.Success($"✓ {summary}") : Styling.Warning(summary));

        if (result.DestructiveFixesSkipped > 0)
            AnsiConsole.MarkupLine(
                $"  {Styling.Warning($"{result.DestructiveFixesSkipped} destructive fixes skipped")}"
                + Styling.Muted(" — they delete objects; add --allow-delete to apply"));

        if (result.FixErrors is { Count: > 0 })
        {
            AnsiConsole.MarkupLine($"  {Styling.Error("Fix errors:")}");
            foreach (var err in result.FixErrors)
                AnsiConsole.MarkupLine("    {0}", Styling.MarkupEscape(err));
        }

        if (result.FixesApplied == 0)
            return;

        switch (result.FixOutcome.Status)
        {
            case MutationStatus.Saved:
                MutationOutput.RenderSaved(result.FixOutcome, "  ");
                break;
            case MutationStatus.Staged:
                AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");
                break;
            default:
                // Without --save/--stage the fixes only touched the in-memory copy.
                HintConsole().MarkupLine("  " + Styling.Warning("Not saved.") + " "
                    + Styling.Guidance("Persist with:") + " "
                    + Styling.Option(BpaRunView.HintCommand(view.CommandTokens, "--fix", "--save"))
                    + Styling.Guidance("  (or --stage)"));
                break;
        }

        MutationOutput.RenderSync(result.FixOutcome, "  ");
    }

    /// <summary>Copy-pasteable next steps, on stderr so piped output stays results-only.</summary>
    private static void RenderHints(
        BpaRunResult result, IReadOnlyList<BpaRunView.RuleGroup> visible, BpaRunView.RunOptions view)
    {
        var hints = new List<(string Label, string Command)>(3);

        var fixable = result.Violations.Count(v => v.CanFix);
        if (fixable > 0)
            hints.Add(($"Fix {fixable} {(fixable == 1 ? "finding" : "findings")}:",
                BpaRunView.HintCommand(view.CommandTokens, "--fix", "--save")));

        if (!view.Details)
            hints.Add(("Details:", BpaRunView.HintCommand(view.CommandTokens, "--details")));
        else if (!view.Full && visible.Any(g => g.Objects.Count > BpaRunView.DefaultObjectCap))
            hints.Add(("Every object:", BpaRunView.HintCommand(view.CommandTokens, "--full")));

        if (visible.Count > 1)
            hints.Add(("One rule:", BpaRunView.HintCommand(view.CommandTokens, "--rule", visible[0].RuleId)));

        if (hints.Count == 0)
            return;

        var err = HintConsole();
        err.WriteLine();
        var labelWidth = hints.Max(h => h.Label.Length);
        foreach (var (label, command) in hints)
            err.MarkupLine(Styling.Guidance(label.PadRight(labelWidth)) + "  " + Styling.Option(command));
    }

    /// <summary>
    /// Stderr console that never hard-wraps, so a long suggested command stays one
    /// copy-pasteable line (the terminal soft-wraps it instead).
    /// </summary>
    internal static IAnsiConsole HintConsole()
    {
        var err = StdErr.Console();
        err.Profile.Width = int.MaxValue;
        return err;
    }

    /// <summary>
    /// Footer for the non-violation result kinds. Always shows a one-line count summary when any
    /// are present; lists the individual diagnostics only under --details so default output stays
    /// violation-focused.
    /// </summary>
    private static void RenderDiagnostics(BpaRunResult result, BpaRunView.RunOptions view)
    {
        if (result.RuleLoadDiagnostics is { Count: > 0 } loadDiagnostics)
        {
            var err = StdErr.Console();
            err.WriteLine();
            err.MarkupLine($"  {Styling.Warning("Rule loading:")}");
            foreach (var diag in loadDiagnostics)
                err.MarkupLine($"    {Styling.MarkupEscape(diag)}");
        }

        var parts = new List<string>(4);
        if (result.RuleErrors > 0) parts.Add($"{result.RuleErrors} rule errors");
        if (result.DisabledRules > 0) parts.Add($"{result.DisabledRules} disabled");
        if (result.InvalidCompatibilityRules > 0) parts.Add($"{result.InvalidCompatibilityRules} skipped (compat level)");
        if (result.IgnoredViolations > 0) parts.Add($"{result.IgnoredViolations} ignored");

        if (parts.Count == 0)
            return;

        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Diagnostics:", string.Join(" · ", parts))}");

        if (!view.Details)
        {
            HintConsole().MarkupLine("  " + Styling.Guidance("List them:") + " "
                + Styling.Option(BpaRunView.HintCommand(view.CommandTokens, "--details")));
            return;
        }

        var diagnostics = result.Results
            .Where(r => r.Kind != BpaResultKind.Violation)
            .ToList();

        foreach (var diag in diagnostics)
        {
            var label = diag.Kind switch
            {
                BpaResultKind.CompilationError => "compile",
                BpaResultKind.EvaluationError => "evaluate",
                BpaResultKind.InvalidCompatibilityLevel => "compat",
                BpaResultKind.DisabledRule => "disabled",
                _ => diag.Kind.ToString()
            };

            var scope = string.IsNullOrWhiteSpace(diag.ErrorScope) ? "" : $" ({diag.ErrorScope})";
            var detail = string.IsNullOrWhiteSpace(diag.ErrorMessage) ? "" : $" — {diag.ErrorMessage}";
            AnsiConsole.MarkupLine(
                "    {0} {1}{2}{3}",
                Styling.Muted($"[{label}]"),
                Styling.MarkupEscape(diag.RuleId),
                Styling.MarkupEscape(scope),
                Styling.Muted(detail));
        }
    }

    /// <summary>
    /// JSON projection for <c>bpa run</c>. Property names and order are the output contract —
    /// keep stable (guarded by BpaJsonContractTests).
    /// </summary>
    internal static object ToJson(BpaRunResult result)
        => new
        {
            rulesEvaluated = result.RulesEvaluated,
            violations = result.Violations.Count,
            ruleErrors = result.RuleErrors,
            ignoredRules = result.IgnoredViolations,
            disabledRules = result.DisabledRules,
            invalidCompatibilityRules = result.InvalidCompatibilityRules,
            fixesApplied = result.FixesApplied,
            fixesSkipped = result.FixesSkipped,
            destructiveFixesSkipped = result.DestructiveFixesSkipped,
            fixErrors = result.FixErrors ?? Array.Empty<string>(),
            ruleLoadDiagnostics = result.RuleLoadDiagnostics ?? Array.Empty<string>(),
            status = result.FixOutcome.Status,
            saved = result.FixOutcome.Saved,
            savedTo = result.FixOutcome.SavedTo,
            persistence = result.FixOutcome.Persistence,
            target = result.FixOutcome.Target,
            sync = result.FixOutcome.Sync ?? SyncOutcome.NotAttempted,
            newValidationErrors = result.FixOutcome.Validation?.NewErrorCount,
            results = result.Violations.Select(v => new
            {
                ruleId = v.RuleId,
                ruleName = v.RuleName,
                category = v.Category,
                severity = (int)v.Severity,
                severityLabel = v.Severity.ToString(),
                objectName = v.ObjectName,
                objectType = v.ObjectType,
                canFix = v.CanFix
            }),
            diagnostics = result.Results
                .Where(r => r.Kind != BpaResultKind.Violation)
                .Select(r => new
                {
                    kind = r.Kind.ToString(),
                    ruleId = r.RuleId,
                    ruleName = r.RuleName,
                    scope = r.ErrorScope,
                    message = r.ErrorMessage
                }),
            errors = Array.Empty<string>()
        };

    /// <summary>
    /// TRX projection: one Failed test per violated rule (message lists the violating objects;
    /// unevaluable rules surface here with their reason), or a single Passed test summarising
    /// the run when no rule fired, so an all-green run still shows up in CI.
    /// </summary>
    public static IReadOnlyList<TrxWriter.TrxTest> ToTrxTests(BpaRunResult result)
    {
        var tests = new List<TrxWriter.TrxTest>();

        foreach (var group in result.Violations.GroupBy(v => (v.RuleId, v.RuleName)))
        {
            var objects = group
                .Where(v => !string.IsNullOrWhiteSpace(v.ObjectPath))
                .Select(v => $"{v.ObjectType} '{v.ObjectPath}'")
                .ToList();
            var description = CollapseDescription(group.First().Description);
            var message = string.IsNullOrEmpty(description)
                ? string.Join(Environment.NewLine, objects)
                : $"{description}{Environment.NewLine}{string.Join(Environment.NewLine, objects)}";

            tests.Add(new TrxWriter.TrxTest(
                $"{group.Key.RuleName} [{group.Key.RuleId}]",
                TrxWriter.TrxOutcome.Failed,
                message));
        }

        // One Error test per rule that only produced compile/evaluation sentinels — collapsed
        // across scopes and already covered by the rule's Failed test (the projected finding
        // carries the reason), so a broken rule yields a single outcome instead of two tests
        // with one name. Rules with real objects keep their Error test: their Failed message
        // lists the objects, not the evaluation failure.
        var rulesWithRealObjects = result.Violations
            .Where(v => !string.IsNullOrWhiteSpace(v.ObjectPath))
            .Select(v => v.RuleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in result.Results
            .Where(r => r.Kind is BpaResultKind.CompilationError or BpaResultKind.EvaluationError)
            .GroupBy(r => (r.RuleId, r.RuleName))
            .Where(g => rulesWithRealObjects.Contains(g.Key.RuleId)))
        {
            var messages = group
                .Select(s => s.ErrorScope is null ? s.ErrorMessage : $"{s.ErrorScope}: {s.ErrorMessage}")
                .Where(m => !string.IsNullOrEmpty(m));

            tests.Add(new TrxWriter.TrxTest(
                $"{group.Key.RuleName} [{group.Key.RuleId}]",
                TrxWriter.TrxOutcome.Error,
                string.Join(Environment.NewLine, messages)));
        }

        if (tests.Count == 0)
            tests.Add(new TrxWriter.TrxTest(
                $"Best Practice Analyzer ({result.RulesEvaluated} rules)",
                TrxWriter.TrxOutcome.Passed));

        return tests;
    }

    public static void EmitCi(string? ci, IReadOnlyList<BpaViolation> violations)
    {
        var annotations = violations
            .Select(v =>
            {
                // Rule-error findings have no model object; skip the empty object segment.
                var msg = string.IsNullOrWhiteSpace(v.ObjectName)
                    ? v.RuleName
                    : $"{v.RuleName}: {v.ObjectType} '{v.ObjectName}'";
                if (!string.IsNullOrWhiteSpace(v.Description))
                    msg += $" - {CollapseDescription(v.Description)}";
                return new CiAnnotation(v.Severity == BpaSeverity.Error, $"{msg} [{v.RuleId}]");
            })
            .ToList();

        CiAnnotations.Emit(ci, annotations, Console.Error);
    }

    private static string CollapseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";

        var firstLine = description.Split('\n', 2)[0].TrimEnd('\r');

        var refIdx = firstLine.IndexOf("Reference:", StringComparison.OrdinalIgnoreCase);
        if (refIdx > 0)
            firstLine = firstLine[..refIdx].TrimEnd();

        return firstLine;
    }
}
