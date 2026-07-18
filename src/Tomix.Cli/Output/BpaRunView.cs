using Tomix.Core.Bpa;

namespace Tomix.Cli.Output;

/// <summary>
/// Pure, presentation-only helpers for the grouped <c>bpa run</c> text output.
/// Kept free of Spectre/console dependencies so the grouping, ordering, and
/// object-list logic can be unit tested directly.
/// </summary>
internal static class BpaRunView
{
    internal const int DefaultObjectCap = 10;

    /// <summary>Display flags for the <c>bpa run</c> text output.</summary>
    internal sealed record RunOptions(
        bool NoMultiline,
        bool Full,
        bool Details,
        bool Errors,
        bool Warnings,
        bool Info);

    /// <summary>One rule and every object that violated it, in display order.</summary>
    internal sealed record RuleGroup(
        string RuleId,
        string RuleName,
        string Category,
        BpaSeverity Severity,
        string? Description,
        IReadOnlyList<string> Objects);

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
                    g.Select(v => v.ObjectName).ToList());
            })
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.RuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Joins object names with <c>·</c>. When not <paramref name="full"/> and the list
    /// exceeds <paramref name="cap"/>, shows the first <paramref name="cap"/> then
    /// <c>… +N more</c>. Returns raw text — callers escape for markup.
    /// </summary>
    internal static string FormatObjectList(IReadOnlyList<string> names, bool full, int cap = DefaultObjectCap)
    {
        if (names.Count == 0)
            return "";

        if (full || names.Count <= cap)
            return string.Join(" · ", names);

        var remaining = names.Count - cap;
        return string.Join(" · ", names.Take(cap)) + $" · … +{remaining} more";
    }

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
    internal static bool MatchesFilter(BpaSeverity severity, bool errors, bool warnings, bool info)
    {
        if (!errors && !warnings && !info)
            return true;

        return (errors && severity == BpaSeverity.Error)
            || (warnings && severity == BpaSeverity.Warning)
            || (info && severity == BpaSeverity.Info);
    }

    internal static string SeverityWord(BpaSeverity severity) => severity switch
    {
        BpaSeverity.Error => "Error",
        BpaSeverity.Warning => "Warning",
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
