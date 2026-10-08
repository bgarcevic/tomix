using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Configuration;
using Tomix.Core.Rules;

namespace Tomix.Cli.Output;

/// <summary>
/// Pure, presentation-only helpers for the grouped <c>bpa run</c> text output.
/// Kept free of Spectre/console dependencies so the grouping, ordering, and
/// object-list logic can be unit tested directly.
/// </summary>
internal static class BpaRunView
{
    internal const int DefaultObjectCap = 10;

    /// <summary>
    /// Display flags for the <c>bpa run</c> text output. <paramref name="CommandTokens"/> is the
    /// command line as parsed (without the <c>tx</c> prefix), echoed back in next-step hints so
    /// they stay copy-pasteable; <c>null</c> falls back to plain <c>bpa run</c>.
    /// </summary>
    internal sealed record RunOptions(
        bool NoMultiline,
        bool Full,
        bool Details,
        bool Errors,
        bool Warnings,
        bool Info,
        IReadOnlyList<string>? CommandTokens = null,
        bool CanCollectVertipaqStats = false);

    /// <summary>One rule and every object that violated it, in display order.</summary>
    internal sealed record RuleGroup(
        string RuleId,
        string RuleName,
        string Category,
        RuleSeverity Severity,
        string? Description,
        IReadOnlyList<string> Objects,
        int FixableCount = 0);

    /// <summary>The rule groups of one severity, with the number of objects they flag.</summary>
    internal sealed record SeveritySection(
        RuleSeverity Severity,
        IReadOnlyList<RuleGroup> Groups,
        int ObjectCount);

    /// <summary>
    /// Collapses violations into one group per rule, ordered by severity
    /// (Error → Warning → Info), then category, then rule name.
    /// </summary>
    internal static IReadOnlyList<RuleGroup> OrderRuleGroups(IEnumerable<BpaViolation> violations)
        => violations
            .GroupBy(v => v.RuleId)
            .Select(g =>
            {
                var first = g.First();
                return new RuleGroup(
                    first.RuleId,
                    first.RuleName,
                    first.Category,
                    first.Severity,
                    first.Description,
                    g.Select(v => v.ObjectName).ToList(),
                    g.Count(v => v.CanFix));
            })
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.RuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Splits ordered groups into one section per severity (Error → Warning → Info), skipping
    /// severities with no groups.
    /// </summary>
    internal static IReadOnlyList<SeveritySection> SeveritySections(IEnumerable<RuleGroup> groups)
        => groups
            .GroupBy(g => g.Severity)
            .OrderByDescending(g => g.Key)
            .Select(g => new SeveritySection(g.Key, g.ToList(), g.Sum(r => r.Objects.Count)))
            .ToList();

    /// <summary>
    /// Object names for the detail view, one per line. When not <paramref name="full"/> and the
    /// list exceeds <paramref name="cap"/>, keeps the first <paramref name="cap"/> and appends
    /// <c>… +N more</c>. Returns raw text — callers escape for markup.
    /// </summary>
    internal static IReadOnlyList<string> ObjectLines(
        IReadOnlyList<string> names, bool full, int cap = DefaultObjectCap, Glyphs? glyphs = null)
    {
        // Rule-error findings carry no object name; they must not render an empty bullet.
        var present = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (full || present.Count <= cap)
            return present;

        return [.. present.Take(cap), $"{(glyphs ?? Glyphs.Unicode).Ellipsis} +{present.Count - cap} more"];
    }

    /// <summary>
    /// The fix marker for a rule: <c>fixable</c> when every object can be fixed,
    /// <c>N fixable</c> when only some can, empty when none can.
    /// </summary>
    internal static string FixableLabel(RuleGroup group)
        => group.FixableCount <= 0 ? ""
            : group.FixableCount >= group.Objects.Count ? "fixable"
            : $"{group.FixableCount} fixable";

    /// <summary>
    /// The status shown for a listed rule: empty when it runs, otherwise every level that keeps
    /// it off — "ignored (you)" (<c>ignore --user</c>), "ignored (model)", or both.
    /// </summary>
    internal static string RuleStatusLabel(BpaRuleInfo rule)
        => (rule.Disabled, rule.Ignored) switch
        {
            (true, true) => "ignored (you, model)",
            (true, false) => "ignored (you)",
            (false, true) => "ignored (model)",
            // A caller that set only Status (not the flags) still gets its value shown.
            _ => rule.Status == "active" ? "" : rule.Status
        };

    /// <summary>
    /// The footer's "Ignored:" value, e.g. <c>1 rule by you · 2 rules by the model · 3 findings</c>:
    /// whole rules per level, then findings an object-level annotation hid. Empty when none.
    /// </summary>
    internal static string IgnoredLine(int rulesByUser, int rulesByModel, int findings, Glyphs? glyphs = null)
    {
        var parts = new List<string>(3);
        if (rulesByUser > 0) parts.Add($"{Plural(rulesByUser, "rule")} by you");
        if (rulesByModel > 0) parts.Add($"{Plural(rulesByModel, "rule")} by the model");
        if (findings > 0) parts.Add(Plural(findings, "finding"));
        return string.Join((glyphs ?? Glyphs.Unicode).Separator, parts);

        static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// "overrides standard" for a listed rule that replaced another source's copy. A preset
    /// overriding a preset (<c>full</c> over <c>standard</c>) is the same rule and says nothing,
    /// so it is left out; a team or model rule replacing a built-in one is worth a mention.
    /// </summary>
    internal static string OverridesLabel(BpaRuleInfo rule)
    {
        if (rule.Overrides is not { Count: > 0 } overrides || IsPreset(rule.Source))
            return "";

        return $"overrides {string.Join(", ", overrides)}";

        static bool IsPreset(string source)
            => BpaRuleLoader.KnownRulesets.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Which level(s) switched a whole rule off, for <c>bpa run --details</c>.</summary>
    internal static string SuppressionLabel(BpaRuleSuppression suppressedBy)
    {
        var levels = new List<string>(2);
        if (suppressedBy.HasFlag(BpaRuleSuppression.User)) levels.Add("ignored by you");
        if (suppressedBy.HasFlag(BpaRuleSuppression.Model)) levels.Add("ignored by the model");
        return string.Join(", ", levels);
    }

    /// <summary>
    /// The attribution line (#233), e.g. <c>Rules loaded: 29 from standard ruleset (26) +
    /// ci/rules.json via TOMIX_BPA_RULES (2) + model annotations (1)</c>. Each count is the
    /// effective rules that source contributed after overrides; the model's embedded and
    /// external-file rules collapse into one "model annotations" entry.
    /// </summary>
    internal static string RulesLoadedLine(IReadOnlyList<BpaRuleSourceSummary> sources)
    {
        if (sources.Count == 0)
            return "Rules loaded: none";

        var parts = new List<string>();
        var modelIndex = -1;
        foreach (var source in sources)
        {
            // The model's sources collapse into one entry, placed where the first one appeared.
            if (source.Origin == BpaRuleOrigin.Model)
            {
                if (modelIndex < 0)
                {
                    modelIndex = parts.Count;
                    parts.Add("");
                }

                continue;
            }

            var count = SourceCount(source.Rules, source.Overridden);
            parts.Add(source.Origin switch
            {
                BpaRuleOrigin.Ruleset => $"{source.Name} ruleset ({count})",
                BpaRuleOrigin.UserFile => $"user rules file ({count})",
                BpaRuleOrigin.Config => $"{source.Name} via {ConfigKeys.BpaRules} ({count})",
                BpaRuleOrigin.Environment => $"{source.Name} via {BpaRuleSources.EnvironmentVariable} ({count})",
                _ => $"{source.Name} via --rules ({count})",
            });
        }

        if (modelIndex >= 0)
        {
            var model = sources.Where(s => s.Origin == BpaRuleOrigin.Model).ToList();
            parts[modelIndex] = $"model annotations ({SourceCount(model.Sum(s => s.Rules), model.Sum(s => s.Overridden))})";
        }

        return $"Rules loaded: {sources.Sum(s => s.Rules)} from {string.Join(" + ", parts)}";
    }

    /// <summary>
    /// A source's count in the attribution line: the rules in effect, plus how many a
    /// higher-precedence source replaced — "26", "25; 1 overridden", or "0; all 26 overridden".
    /// </summary>
    private static string SourceCount(int rules, int overridden)
        => (rules, overridden) switch
        {
            (_, <= 0) => $"{rules}",
            (0, _) => $"0; all {overridden} overridden",
            _ => $"{rules}; {overridden} overridden"
        };

    /// <summary>
    /// The one-line run summary, e.g. <c>3 errors · 32 warnings in 5 of 27 rules · 22 passed · 326ms</c>
    /// or <c>All 27 rules passed · 326ms</c>. Zero severity counts are left out. Rules that were
    /// <paramref name="notChecked"/> (no VertiPaq statistics) are never counted as passed.
    /// </summary>
    internal static string SummaryLine(
        int errors, int warnings, int info, int failedRules, int rulesEvaluated, long durationMs, int notChecked = 0,
        Glyphs? glyphs = null)
    {
        var sep = (glyphs ?? Glyphs.Unicode).Separator;
        var duration = durationMs > 0 ? $"{sep}{durationMs}ms" : "";
        var skipped = notChecked > 0 ? $"{sep}{notChecked} not checked" : "";
        if (failedRules == 0)
            return notChecked == 0
                ? $"All {rulesEvaluated} {Plural(rulesEvaluated, "rule")} passed{duration}"
                : $"{Math.Max(0, rulesEvaluated - notChecked)} of {Count(rulesEvaluated, "rule")} passed{skipped}{duration}";

        var counts = new List<string>(3);
        if (errors > 0) counts.Add(Count(errors, "error"));
        if (warnings > 0) counts.Add(Count(warnings, "warning"));
        if (info > 0) counts.Add($"{info} info");

        // Rule-error findings can come from rules outside the evaluated count; never go negative.
        var total = Math.Max(rulesEvaluated, failedRules);
        var passed = Math.Max(0, total - failedRules - notChecked);
        return $"{string.Join(sep, counts)} in {failedRules} of {Count(total, "rule")}{sep}{passed} passed{skipped}{duration}";
    }

    /// <summary>
    /// Packs segments into lines of at most <paramref name="width"/> columns, joined by
    /// <c> · </c>. A segment is never split, so a long rule ID stays whole on its own line.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> PackSegments(IEnumerable<string> segments, int width)
    {
        var lines = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var length = 0;
        foreach (var segment in segments.Where(s => s.Length > 0))
        {
            if (current.Count > 0 && length + 3 + segment.Length > width)
            {
                lines.Add(current);
                current = [];
                length = 0;
            }

            length += (current.Count > 0 ? 3 : 0) + segment.Length;
            current.Add(segment);
        }

        if (current.Count > 0)
            lines.Add(current);
        return lines;
    }

    private static readonly HashSet<string> HintDroppedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--details", "--full", "--no-multiline", "--errors", "--warnings", "--info",
        "--fix", "--allow-delete", "--save", "--stage", "--revert", "--quiet", "-q",
        "--yes", "-y", "--force", "-f", "--overwrite", "--no-sync",
    };

    // Options whose value is dropped with them: a hint must never re-save to the same target.
    private static readonly HashSet<string> HintDroppedValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--save-to", "--serialization", "--trx", "--ci",
    };

    /// <summary>
    /// A copy-pasteable <c>tx …</c> command: the user's own command line (so model, ruleset,
    /// and scope carry over) minus display, fix, persistence, and report flags, plus
    /// <paramref name="extra"/>.
    /// </summary>
    internal static string HintCommand(IReadOnlyList<string>? tokens, params string[] extra)
    {
        var kept = new List<string>();
        var source = tokens ?? ["bpa", "run"];
        for (var i = 0; i < source.Count; i++)
        {
            var equals = source[i].IndexOf('=');
            var option = equals < 0 ? source[i] : source[i][..equals];
            if (HintDroppedValueOptions.Contains(option))
            {
                if (equals < 0)
                    i++;
            }
            else if (!HintDroppedFlags.Contains(option))
                kept.Add(source[i]);
        }

        return "tx " + string.Join(" ", kept.Concat(extra).Select(QuoteToken));
    }

    /// <summary>Quotes a command argument literally for the host shell used by <c>tx</c> hints.</summary>
    internal static string QuoteToken(string token)
    {
        if (token.Length > 0 && token.All(c => char.IsAsciiLetterOrDigit(c)
            || c is '_' or '-' or '.' or '/' or ':' or '='))
            return token;

        // Single quotes suppress variable and command expansion in both PowerShell and POSIX
        // shells. Their embedded-quote escaping differs, so use the host platform's syntax.
        return OperatingSystem.IsWindows()
            ? "'" + token.Replace("'", "''") + "'"
            : "'" + token.Replace("'", "'\\''") + "'";
    }

    private const int FixValueWidth = 60;

    /// <summary>
    /// One pending fix for the <c>--fix</c> preview: the headline
    /// (<c>Would fix: Column 'Sales/Amount' — RULE_ID</c>, or <c>Would delete:</c>) and, for
    /// a property set, the change (<c>FormatString: "" → "#,##0"</c>). Values stay on one line
    /// and are cut at a fixed width, so a rewritten expression cannot flood the preview.
    /// </summary>
    internal static (string Headline, string? Change) PendingFix(BpaFixChange change, Glyphs? glyphs = null)
    {
        glyphs ??= Glyphs.Unicode;
        var verb = change.Action == BpaFixAction.Delete ? "Would delete:" : "Would fix:";
        var headline = $"{verb} {change.ObjectType} '{change.ObjectPath}'{glyphs.Dash}{change.RuleId}";
        if (change.Action == BpaFixAction.Delete || change.Property is null)
            return (headline, null);

        return (headline, $"{change.Property}: {FixValue(change.Before, glyphs)}{glyphs.Arrow}{FixValue(change.After, glyphs)}");
    }

    private static string FixValue(string? value, Glyphs glyphs)
    {
        if (value is null)
            return "(unset)";

        var flat = string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        if (flat.Length > FixValueWidth)
            flat = flat[..(FixValueWidth - glyphs.Ellipsis.Length)] + glyphs.Ellipsis;
        return $"\"{flat}\"";
    }

    private static string Count(int n, string noun) => $"{n} {Plural(n, noun)}";

    private static string Plural(int n, string noun) => n == 1 ? noun : noun + "s";

    /// <summary>
    /// Drops the leading <c>[Category]</c> segment from a rule name when it duplicates
    /// the rule's category (which is already shown in the section header).
    /// </summary>
    internal static string StripCategoryPrefix(string ruleName, string category)
    {
        if (ruleName.Length == 0 || ruleName[0] != '[')
            return ruleName;

        var end = ruleName.IndexOf(']');
        if (end <= 0)
            return ruleName;

        var prefix = ruleName.Substring(1, end - 1).Trim();
        return string.Equals(prefix, category, StringComparison.OrdinalIgnoreCase)
            ? ruleName[(end + 1)..].TrimStart()
            : ruleName;
    }

    /// <summary>
    /// Returns the rule guidance with any trailing <c>Reference:</c> link removed.
    /// When <paramref name="collapse"/> is set, only the first line is kept
    /// (the <c>--no-multiline</c> behavior).
    /// </summary>
    internal static string Guidance(string? description, bool collapse)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";

        var text = description;

        var refIdx = text.IndexOf("Reference:", StringComparison.OrdinalIgnoreCase);
        if (refIdx > 0)
            text = text[..refIdx].TrimEnd();

        return collapse
            ? text.Split('\n', 2)[0].TrimEnd('\r')
            : text.Replace("\r\n", "\n").Trim();
    }

    /// <summary>
    /// Whether a severity should be shown given the display filters. When no flag is set,
    /// everything is shown.
    /// </summary>
    internal static bool MatchesFilter(RuleSeverity severity, bool errors, bool warnings, bool info)
    {
        if (!errors && !warnings && !info)
            return true;

        return (errors && severity == RuleSeverity.Error)
            || (warnings && severity == RuleSeverity.Warning)
            || (info && severity == RuleSeverity.Info);
    }

    internal static string SeverityWord(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "Error",
        RuleSeverity.Warning => "Warning",
        _ => "Info"
    };

    /// <summary>
    /// Word-wraps <paramref name="text"/> to at most <paramref name="width"/> columns, splitting
    /// only at whitespace and preserving existing line breaks. A single word longer than the
    /// width is kept on its own (over-long) line rather than split mid-word. Used to constrain
    /// guidance/object text to a readable measure on wide terminals.
    /// </summary>
    internal static IReadOnlyList<string> WrapText(string text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            if (!string.IsNullOrEmpty(text))
                lines.Add(text);
            return lines;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add("");
                continue;
            }

            var current = "";
            foreach (var word in words)
            {
                if (current.Length == 0)
                    current = word;
                else if (current.Length + 1 + word.Length <= width)
                    current += " " + word;
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }

            if (current.Length > 0)
                lines.Add(current);
        }

        return lines;
    }
}
