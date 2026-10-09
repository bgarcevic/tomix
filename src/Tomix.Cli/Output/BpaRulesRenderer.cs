using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;

namespace Tomix.Cli.Output;

/// <summary>
/// Spectre rendering and JSON projections for the <c>bpa rules</c> subcommands
/// (list, show, disable/enable, ignore/unignore, add/set/remove/init).
/// </summary>
internal static class BpaRulesRenderer
{
    private const int MaxTextWidth = 84;

    /// <summary>
    /// <c>bpa rules list</c>: one section per category, each rule as a severity dot and its name
    /// over a muted metadata line (ID · severity · scope · source · status · fixable) that never splits
    /// the ID. Source is shown only when the listing mixes sources.
    /// </summary>
    public static void RenderList(BpaRulesListResult result) => RenderList(result, null);

    public static void RenderList(BpaRulesListResult result, BpaRulesListRequest? request)
    {
        if (result.Rules.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Warning("No BPA rules to show."));
            RenderListSummary(result);
            RenderListDiagnostics(result);
            return;
        }

        var width = Math.Max(24, Math.Min(MaxTextWidth, AnsiConsole.Profile.Width) - 4);
        var showSource = result.Rules.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        var first = true;
        foreach (var category in result.Rules
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!first)
                AnsiConsole.WriteLine();
            first = false;

            var count = category.Count();
            AnsiConsole.MarkupLine(
                $"{Styling.Bold(category.Key)}  {Styling.Muted(count == 1 ? "1 rule" : $"{count} rules")}");

            foreach (var rule in category
                .OrderByDescending(r => r.Severity)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = BpaRunView.StripCategoryPrefix(rule.Name, rule.Category);
                var nameLines = BpaRunView.WrapText(name, width);
                for (var i = 0; i < nameLines.Count; i++)
                    AnsiConsole.MarkupLine(
                        (i == 0 ? $"  {SeverityDot(rule.Severity)} " : "    ") + Styling.MarkupEscape(nameLines[i]));

                RenderMetaLine(rule, width, "    ", showSource);
            }
        }

        AnsiConsole.WriteLine();
        RenderListSummary(result);
        RenderListDiagnostics(result);

        BpaRunRenderer.HintConsole().MarkupLine(
            Styling.Guidance("Show a rule:") + "  " + Styling.Option(ShowHint(result.Rules[0].Id, request)));
    }

    /// <summary>Shows the selected rule with the same model and rule sources as the listing.</summary>
    internal static string ShowHint(string ruleId, BpaRulesListRequest? request)
    {
        var args = new List<string> { "bpa", "rules" };
        if (!string.IsNullOrWhiteSpace(request?.RulesFile))
            args.AddRange(["--rules-file", request.RulesFile]);

        args.AddRange(["show", ruleId]);
        if (request?.Model is { Value: { Length: > 0 } model })
        {
            args.Add(model);
            if (!string.IsNullOrWhiteSpace(request.Model.Database))
                args.AddRange(["--database", request.Model.Database]);
        }

        if (!string.IsNullOrWhiteSpace(request?.Ruleset))
            args.AddRange(["--ruleset", request.Ruleset]);
        if (request?.NoDefaults == true)
            args.Add("--no-defaults");

        return "tx " + string.Join(" ", args.Select(BpaRunView.QuoteToken));
    }

    /// <summary>
    /// <c>bpa rules show</c>: everything about one rule. A rule defined by more than one source
    /// (a model rule overriding the ruleset) is shown once per source.
    /// </summary>
    public static void RenderShow(BpaRulesListResult result)
    {
        var width = Math.Max(24, Math.Min(MaxTextWidth, AnsiConsole.Profile.Width));
        // Text is wrapped here; lift Spectre's own wrapping so a long reference URL or
        // expression line stays whole (and clickable) instead of being hard-split.
        var originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            for (var r = 0; r < result.Rules.Count; r++)
            {
                if (r > 0)
                    AnsiConsole.WriteLine();
                RenderRuleDetail(result.Rules[r], width);
            }
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth;
        }

        RenderListDiagnostics(result);
    }

    private static void RenderRuleDetail(BpaRuleInfo rule, int width)
    {
        const string labelPad = "            ";

        var name = BpaRunView.StripCategoryPrefix(rule.Name, rule.Category);
        foreach (var line in BpaRunView.WrapText(name, width))
            AnsiConsole.MarkupLine(Styling.Bold(line));
        AnsiConsole.MarkupLine(string.Join(Styling.Muted(Styling.Glyphs.Separator),
            Styling.Muted(rule.Id),
            Styling.Muted(rule.Category),
            $"{SeverityDot(rule.Severity)} {Styling.Muted(BpaRunView.SeverityWord(rule.Severity).ToLowerInvariant())}"));

        var (text, reference) = SplitReference(rule.Description);
        if (text.Length > 0 || reference is not null)
            AnsiConsole.WriteLine();
        foreach (var line in BpaRunView.WrapText(text, width))
            AnsiConsole.MarkupLine(Styling.MarkupEscape(line));
        if (reference is not null)
            AnsiConsole.MarkupLine($"{Styling.Muted("Reference:")} {Styling.Path(reference)}");

        AnsiConsole.WriteLine();
        RenderField("Source", [rule.Source], labelPad);
        if (BpaRunView.RuleStatusLabel(rule) is { Length: > 0 } status)
            AnsiConsole.MarkupLine(Styling.Muted("Status".PadRight(labelPad.Length)) + Styling.Warning(status));
        RenderField("Applies to", [rule.Scope], labelPad);
        RenderField("Expression", ExpressionLines(rule.Expression), labelPad);
        RenderField("Fix", ExpressionLines(rule.FixExpression), labelPad);
    }

    private static void RenderField(string label, IReadOnlyList<string> lines, string pad)
    {
        if (lines.Count == 0 || lines.All(string.IsNullOrWhiteSpace))
            return;

        for (var i = 0; i < lines.Count; i++)
        {
            var lead = i == 0 ? Styling.Muted(label.PadRight(pad.Length)) : pad;
            AnsiConsole.MarkupLine(lead + Styling.MarkupEscape(lines[i]));
        }
    }

    private static void RenderMetaLine(BpaRuleInfo rule, int width, string indent, bool showSource)
    {
        var status = BpaRunView.RuleStatusLabel(rule);
        var fixable = string.IsNullOrWhiteSpace(rule.FixExpression) ? "" : "fixable";
        var segments = new List<string> { rule.Id };
        segments.Add(BpaRunView.SeverityWord(rule.Severity).ToLowerInvariant());
        segments.Add(rule.Scope);
        if (showSource) segments.Add(rule.Source);
        segments.Add(BpaRunView.OverridesLabel(rule));
        segments.Add(status);
        segments.Add(fixable);

        foreach (var line in BpaRunView.PackSegments(segments, width))
        {
            var parts = line.Select(s =>
                ReferenceEquals(s, status) ? Styling.Warning(s)
                : ReferenceEquals(s, fixable) ? Styling.Success(s)
                : Styling.Muted(s));
            AnsiConsole.MarkupLine(indent + string.Join(Styling.Muted(Styling.Glyphs.Separator), parts));
        }
    }

    private static void RenderListSummary(BpaRulesListResult result)
    {
        var summary = result.Summary;
        var parts = new List<string> { $"{summary.Active} active" };
        if (summary.Disabled > 0) parts.Add($"{summary.Disabled} ignored by you");
        if (summary.Ignored > 0) parts.Add($"{summary.Ignored} ignored by the model");

        var line = result.Rules.Count < summary.Total
            ? $"{result.Rules.Count} of {summary.Total} rules shown{Styling.Glyphs.Separator}{string.Join(Styling.Glyphs.Separator, parts)}"
            : parts.Count == 1
                ? $"{summary.Total} rules"
                : $"{summary.Total} rules{Styling.Glyphs.Separator}{string.Join(Styling.Glyphs.Separator, parts)}";
        AnsiConsole.MarkupLine(Styling.MarkupEscape(line));

        if (result.Rules.Count < summary.Total)
            StdErr.MarkupLine(Styling.Guidance("Add --all to include ignored rules."));
    }

    private static string SeverityDot(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => Styling.Error(Styling.Glyphs.Bullet),
        RuleSeverity.Warning => Styling.Warning(Styling.Glyphs.Bullet),
        _ => Styling.Muted(Styling.Glyphs.Bullet)
    };

    /// <summary>
    /// Splits rule guidance into its text and the trailing <c>Reference:</c> link, if any.
    /// </summary>
    internal static (string Text, string? Reference) SplitReference(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return ("", null);

        var normalized = description.Replace("\r\n", "\n").Trim();
        var idx = normalized.IndexOf("Reference:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return (normalized, null);

        var reference = normalized[(idx + "Reference:".Length)..].Trim();
        return (normalized[..idx].TrimEnd(), reference.Length > 0 ? reference : null);
    }

    /// <summary>
    /// Tidies a rule expression for display: trims every line, drops blank lines, and joins a
    /// lone <c>and</c>/<c>or</c> onto the line after it so conditions read as one line each.
    /// </summary>
    internal static IReadOnlyList<string> ExpressionLines(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var lines = new List<string>();
        string? pendingOperator = null;
        foreach (var raw in expression.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.Equals("and", StringComparison.OrdinalIgnoreCase) || line.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                pendingOperator = pendingOperator is null ? line : $"{pendingOperator} {line}";
                continue;
            }

            lines.Add(pendingOperator is null ? line : $"{pendingOperator} {line}");
            pendingOperator = null;
        }

        if (pendingOperator is not null)
            lines.Add(pendingOperator);
        return lines;
    }

    private static void RenderListDiagnostics(BpaRulesListResult result)
    {
        if (result.Diagnostics is not { Count: > 0 } diagnostics)
            return;

        foreach (var diagnostic in diagnostics)
            AnsiConsole.MarkupLine($"  {Styling.Warning(diagnostic)}");
    }

    public static void RenderDisable(BpaRulesDisableResult result)
    {
        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"You {(result.Disabled ? "already ignore" : "weren't ignoring")} rule '{result.RuleId}'{Styling.Glyphs.Dash}no change."));
            return;
        }

        AnsiConsole.MarkupLine(result.Disabled
            ? $"Rule {Styling.Value(result.RuleId)} ignored for you, on this machine, for every model."
            : $"Rule {Styling.Value(result.RuleId)} no longer ignored for you.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Rules you ignore:", result.DisabledRuleIds.Count.ToString())}");
    }

    /// <summary><c>bpa rules add/set/remove/init</c>: what changed, and in which file.</summary>
    public static void RenderFile(BpaRulesFileResult result)
    {
        var id = Styling.Value(result.RuleId ?? "");
        var path = Styling.Path(result.Path);
        var rows = new List<(string, string)>();
        switch (result.Action)
        {
            case "init":
                AnsiConsole.MarkupLine($"Created empty rules file {path}.");
                BpaRunRenderer.HintConsole().MarkupLine(Styling.Guidance("Add a rule:") + "  "
                    + Styling.Option("tx bpa rules add --id MY_RULE --name \"...\" --scope Measure --expression \"...\""));
                return;
            case "remove":
                AnsiConsole.MarkupLine($"Removed rule {id} from {path}.");
                break;
            case "set" when !result.Changed:
                AnsiConsole.MarkupLine(Styling.Muted($"Rule '{result.RuleId}' already has those values{Styling.Glyphs.Dash}no change."));
                return;
            case "set":
                AnsiConsole.MarkupLine($"Updated rule {id} in {path}.");
                rows.Add(("Changed:", string.Join(", ", result.ChangedFields ?? [])));
                break;
            default:
                AnsiConsole.MarkupLine($"Added rule {id} to {path}.");
                break;
        }

        if (result.Rule is { } rule)
            rows.Add(("Applies to:", rule.Scope));
        rows.Add(("Rules in file:", result.RuleCount.ToString()));
        Styling.WriteKeyValues(rows, indent: "  ");
    }

    /// <summary><c>bpa rules add/set/remove</c> against a model: what changed, and whether it was kept.</summary>
    public static void RenderModel(BpaRulesModelResult result)
    {
        var id = Styling.Value(result.RuleId);
        var model = Styling.Value(result.ModelName);
        var rows = new List<(string, string)>();
        switch (result.Action)
        {
            case "set" when !result.Changed:
                AnsiConsole.MarkupLine(Styling.Muted($"Rule '{result.RuleId}' already has those values{Styling.Glyphs.Dash}no change."));
                return;
            case "set":
                AnsiConsole.MarkupLine($"Updated model rule {id} in {model}.");
                rows.Add(("Changed:", string.Join(", ", result.ChangedFields ?? [])));
                break;
            case "remove":
                AnsiConsole.MarkupLine($"Removed model rule {id} from {model}.");
                break;
            default:
                AnsiConsole.MarkupLine($"Added model rule {id} to {model}.");
                break;
        }

        if (result.Rule is { } rule)
            rows.Add(("Applies to:", rule.Scope));
        rows.Add(("Rules in model:", result.RuleCount.ToString()));
        Styling.WriteKeyValues(rows, indent: "  ");
        RenderMutationTail(result);
    }

    public static void RenderIgnore(BpaRulesIgnoreResult result)
    {
        var verb = result.Ignored ? "ignored" : "no longer ignored";

        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"Rule '{result.RuleId}' was already {(result.Ignored ? "ignored" : "not ignored")}{Styling.Glyphs.Dash}no change."));
            return;
        }

        AnsiConsole.MarkupLine($"Rule {Styling.Value(result.RuleId)} is now {verb} for {Styling.Value(result.ModelName)}.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Ignored rules:", result.RuleIds.Count.ToString())}");
        RenderMutationTail(result);
    }

    private static void RenderMutationTail(MutationResult result)
    {
        if (result.Saved)
            MutationOutput.RenderSaved(result.Outcome, "  ");
        else if (result.Status == MutationStatus.Staged)
            AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");
        else
            AnsiConsole.MarkupLine($"  {Styling.Muted($"Not saved{Styling.Glyphs.Dash}re-run with --save to persist or --stage to stage.")}");

        MutationOutput.RenderSync(result.Outcome, "  ");
    }

    /// <summary>
    /// JSON projection for <c>bpa rules list</c>. Property names, order, and the conditional
    /// omission of empty fields are the output contract — keep stable (guarded by BpaJsonContractTests).
    /// </summary>
    internal static object ToListJson(BpaRulesListResult result)
    {
        var json = new Dictionary<string, object?>
        {
            ["rules"] = result.Rules.Select(ProjectRuleInfo),
            ["summary"] = result.Summary
        };

        if (result.Diagnostics is { Count: > 0 })
            json["diagnostics"] = result.Diagnostics;

        return json;
    }

    internal static object ToDisableJson(BpaRulesDisableResult result)
        => new
        {
            ruleId = result.RuleId,
            level = "user",
            disabled = result.Disabled,
            changed = result.Changed,
            disabledRuleIds = result.DisabledRuleIds
        };

    /// <summary>
    /// JSON projection for <c>bpa rules add/set/remove/init</c>. <c>rule</c> uses the
    /// <c>bpa rules list</c> rule shape; <c>changedFields</c> appears only for <c>set</c>.
    /// </summary>
    internal static object ToFileJson(BpaRulesFileResult result)
    {
        var json = new Dictionary<string, object?>
        {
            ["action"] = result.Action,
            ["path"] = result.Path,
            ["changed"] = result.Changed,
            ["ruleCount"] = result.RuleCount
        };

        if (result.RuleId is not null)
            json["ruleId"] = result.RuleId;
        if (result.ChangedFields is not null)
            json["changedFields"] = result.ChangedFields;
        if (result.Rule is not null)
            json["rule"] = ProjectRuleInfo(result.Rule);

        return json;
    }

    /// <summary>
    /// JSON projection for <c>bpa rules add/set/remove</c> against a model: the rule fields of
    /// <see cref="ToFileJson"/> with <c>model</c> in place of <c>path</c>, then the mutation fields.
    /// </summary>
    internal static object ToModelJson(BpaRulesModelResult result)
    {
        var json = new Dictionary<string, object?>
        {
            ["action"] = result.Action,
            ["model"] = result.ModelName,
            ["changed"] = result.Changed,
            ["ruleCount"] = result.RuleCount,
            ["ruleId"] = result.RuleId
        };

        if (result.ChangedFields is not null)
            json["changedFields"] = result.ChangedFields;
        if (result.Rule is not null)
            json["rule"] = ProjectRuleInfo(result.Rule);

        json["status"] = result.Status;
        json["saved"] = result.Saved;
        json["savedTo"] = result.SavedTo;
        json["persistence"] = result.Persistence;
        json["target"] = result.Target;
        json["sync"] = result.Sync;
        json["newValidationErrors"] = result.NewValidationErrors;
        return json;
    }

    internal static object ToIgnoreJson(BpaRulesIgnoreResult result)
        => new
        {
            ruleId = result.RuleId,
            level = "model",
            ignored = result.Ignored,
            changed = result.Changed,
            ruleIds = result.RuleIds,
            model = result.ModelName,
            status = result.Status,
            saved = result.Saved,
            savedTo = result.SavedTo,
            persistence = result.Persistence,
            target = result.Target,
            sync = result.Sync,
            newValidationErrors = result.NewValidationErrors
        };

    private static Dictionary<string, object?> ProjectRuleInfo(BpaRuleInfo rule)
    {
        var json = new Dictionary<string, object?>
        {
            ["source"] = rule.Source,
            ["status"] = rule.Status,
            // Both levels, since a rule can be off at both and status names only one.
            ["ignoredByUser"] = rule.Disabled,
            ["ignoredByModel"] = rule.Ignored,
            ["id"] = rule.Id,
            ["name"] = rule.Name,
            ["category"] = rule.Category,
            ["severity"] = (int)rule.Severity,
            ["severityLabel"] = rule.Severity.ToString(),
            ["scope"] = rule.Scope
        };

        // The other sources that define this id; `list` shows the winning copy once (additive).
        if (rule.Overrides is { Count: > 0 })
            json["overrides"] = rule.Overrides;

        AddIfNotEmpty(json, "description", rule.Description);
        AddIfNotEmpty(json, "expression", rule.Expression);
        AddIfNotEmpty(json, "fixExpression", rule.FixExpression);

        return json;
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            json[name] = value;
    }
}
