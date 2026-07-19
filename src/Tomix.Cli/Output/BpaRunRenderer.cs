using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Output;

/// <summary>
/// Spectre rendering for <c>bpa run</c>: grouped violation output (compact table or
/// per-rule detail), diagnostics footer, JSON projection, and CI logging commands.
/// Layout decisions live in <see cref="BpaRunView"/>; this file only formats and prints.
/// </summary>
internal static class BpaRunRenderer
{
    private const int DetailTextWidth = 84;

    public static void Render(BpaRunResult result, BpaRunView.RunOptions view)
    {
        AnsiConsole.MarkupLine(Styling.Title($"BPA analysis · {result.ModelName}"));

        if (result.Violations.Count == 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Styling.Success("No BPA violations found."));
            RenderDiagnostics(result, view);
            return;
        }

        var errorCount = result.Violations.Count(v => v.Severity == BpaSeverity.Error);
        var warningCount = result.Violations.Count(v => v.Severity == BpaSeverity.Warning);
        var infoCount = result.Violations.Count(v => v.Severity == BpaSeverity.Info);

        var groups = BpaRunView.OrderRuleGroups(result.Violations);

        var summary = string.Join(" · ",
            $"{result.Violations.Count} findings",
            Styling.Error($"{errorCount} errors"),
            Styling.Warning($"{warningCount} warnings"),
            $"{infoCount} info",
            $"{groups.Count} rules");

        var filterActive = view.Errors || view.Warnings || view.Info;
        if (filterActive)
        {
            var shown = new List<string>(3);
            if (view.Errors) shown.Add("errors");
            if (view.Warnings) shown.Add("warnings");
            if (view.Info) shown.Add("info");
            summary += "  " + Styling.Muted($"(showing {string.Join(" + ", shown)})");
        }

        AnsiConsole.Write(new Rule().RuleStyle(new Style(Palette.Slate)));

        var visible = groups
            .Where(g => BpaRunView.MatchesFilter(g.Severity, view.Errors, view.Warnings, view.Info))
            .ToList();

        if (visible.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("  Nothing to show for the selected severities."));
        }
        else if (view.Details)
        {
            foreach (var group in visible)
                RenderRuleGroup(group, view);
        }
        else
        {
            RenderCompact(visible);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Palette.Slate)));
        AnsiConsole.MarkupLine(summary);

        var fixable = result.Violations.Count(v => v.CanFix);
        if (fixable > 0)
            AnsiConsole.MarkupLine(
                Styling.Value($"{fixable} of {result.Violations.Count} can be auto-fixed")
                + Styling.Muted(" — run  bpa run --fix"));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Rules evaluated:", result.RulesEvaluated.ToString())}");

        if (result.DurationMs > 0)
            AnsiConsole.MarkupLine($"  {Styling.KeyValue("Duration:", $"{result.DurationMs}ms")}");

        RenderDiagnostics(result, view);

        if (result.FixesApplied > 0)
        {
            AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes applied:", result.FixesApplied.ToString())}");
            if (result.FixesSkipped > 0)
                AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes skipped:", result.FixesSkipped.ToString())}");
            RenderDestructiveSkipped(result);
            if (result.Saved is true or string)
                AnsiConsole.MarkupLine($"  {Styling.Success("Model saved.")}");
            else if (result.Staged == true)
                AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");

            if (result.Synced)
                AnsiConsole.MarkupLine($"  {Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}")}");
            else if (result.SyncWarning is not null)
                AnsiConsole.MarkupLine($"  {Styling.Warning(Styling.MarkupEscape(result.SyncWarning))}");
        }
        else
        {
            if (result.FixesSkipped > 0)
                AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes skipped:", result.FixesSkipped.ToString())}");
            RenderDestructiveSkipped(result);
        }

        if (result.FixErrors is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  {Styling.Error("Fix errors:")}");
            foreach (var err in result.FixErrors)
                AnsiConsole.MarkupLine("    {0}", Styling.MarkupEscape(err));
        }

        if (visible.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Styling.Guidance(view.Details
                ? "Run with --full to list every affected object, or --rule <ID> to focus a single rule."
                : "Run  bpa run --details  for guidance, or  --rule <ID>  to focus a single rule."));
        }
    }

    private static void RenderDestructiveSkipped(BpaRunResult result)
    {
        if (result.DestructiveFixesSkipped > 0)
            AnsiConsole.MarkupLine(
                $"  {Styling.KeyValue("Destructive fixes skipped:", result.DestructiveFixesSkipped.ToString())}"
                + Styling.Muted(" — deletes objects; rerun with  --fix --allow-delete  to apply"));
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
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  {Styling.Warning("Rule loading:")}");
            foreach (var diag in loadDiagnostics)
                AnsiConsole.MarkupLine("    {0}", Styling.MarkupEscape(diag));
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
            AnsiConsole.MarkupLine(Styling.Muted("  Run  bpa run --details  to list diagnostics."));
            return;
        }

        var diagnostics = result.Results
            .Where(r => r.Kind != BpaResultKind.Violation)
            .ToList();

        if (diagnostics.Count == 0)
            return;

        AnsiConsole.WriteLine();
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
                Styling.Muted(Styling.MarkupEscape(detail)));
        }
    }

    private static void RenderCompact(IReadOnlyList<BpaRunView.RuleGroup> groups)
    {
        var table = new Table().Border(TableBorder.None);
        table.AddColumn(new TableColumn(Styling.Muted("SEVERITY")));
        table.AddColumn(new TableColumn(Styling.Muted("CATEGORY")));
        table.AddColumn(new TableColumn(Styling.Muted("RULE / ID")));
        table.AddColumn(new TableColumn(Styling.Muted("COUNT")).RightAligned());

        foreach (var group in groups)
        {
            var name = BpaRunView.StripCategoryPrefix(group.RuleName, group.Category);
            // Rule name on line 1, the (copy-able) rule id dimmed on line 2 of the same cell,
            // so the severity/category/count columns stay aligned regardless of id length.
            var rule = $"{Styling.Bold(name)}\n{Styling.Muted(group.RuleId)}";
            table.AddRow(
                Styling.SeverityHeading(BpaRunView.SeverityWord(group.Severity)),
                Styling.MarkupEscape(group.Category),
                rule,
                Styling.Muted($"×{group.Objects.Count}"));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderRuleGroup(BpaRunView.RuleGroup group, BpaRunView.RunOptions view)
    {
        AnsiConsole.WriteLine();

        // Wrap to the effective render width so Spectre never re-wraps (which would
        // split words). Account for the 2-space indent; cap at DetailTextWidth.
        var width = Math.Max(24, Math.Min(DetailTextWidth, AnsiConsole.Profile.Width - 2));

        var word = BpaRunView.SeverityWord(group.Severity);

        var header = new Grid().Expand();
        header.AddColumn();
        header.AddColumn(new GridColumn().RightAligned());
        header.AddRow(
            $"{Styling.SeverityHeading(word)}  {Styling.MarkupEscape(group.Category)}",
            Styling.Muted($"×{group.Objects.Count}"));
        AnsiConsole.Write(header);

        var name = BpaRunView.StripCategoryPrefix(group.RuleName, group.Category);
        AnsiConsole.MarkupLine($"  {Styling.Bold(name)}  {Styling.Muted($"[{group.RuleId}]")}");

        var guidance = BpaRunView.Guidance(group.Description, view.NoMultiline);
        if (guidance.Length > 0)
            foreach (var line in BpaRunView.WrapText(guidance, width))
                AnsiConsole.MarkupLine($"  {Styling.Guidance(line)}");

        var objects = BpaRunView.FormatObjectList(group.Objects, view.Full);
        if (objects.Length > 0)
            foreach (var line in BpaRunView.WrapText($"Affects  {objects}", width))
                AnsiConsole.MarkupLine($"  {Styling.Muted(line)}");
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
            saved = result.Saved,
            staged = result.Staged,
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
    /// TRX projection: one Failed test per violated rule (message lists the violating objects),
    /// one Error test per compilation/evaluation sentinel, or a single Passed test summarising
    /// the run when no rule fired, so an all-green run still shows up in CI.
    /// </summary>
    public static IReadOnlyList<TrxWriter.TrxTest> ToTrxTests(BpaRunResult result)
    {
        var tests = new List<TrxWriter.TrxTest>();

        foreach (var group in result.Violations.GroupBy(v => (v.RuleId, v.RuleName)))
        {
            var objects = group
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

        // The engine emits one sentinel per evaluated object scope, so a rule that fails in
        // several scopes yields several sentinels — collapse them into one Error test per rule.
        foreach (var group in result.Results
            .Where(r => r.Kind is BpaResultKind.CompilationError or BpaResultKind.EvaluationError)
            .GroupBy(r => (r.RuleId, r.RuleName)))
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
                var msg = $"{v.RuleName}: {v.ObjectType} '{v.ObjectName}'";
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
