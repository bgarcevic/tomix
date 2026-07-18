using Spectre.Console;
using Tomix.App.Bpa;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Output;

/// <summary>
/// Spectre rendering and JSON projections for the <c>bpa rules</c> subcommands
/// (list, disable/enable, ignore/unignore).
/// </summary>
internal static class BpaRulesRenderer
{
    public static void RenderList(BpaRulesListResult result)
    {
        if (result.Rules.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Warning("No BPA rules available."));
            return;
        }

        var table = Styling.NewTable("ID", "Name", "Category", "Severity");

        foreach (var r in result.Rules)
        {
            table.AddRow(
                Styling.MarkupEscape(Truncate(r.Id, 45)),
                Styling.MarkupEscape(Truncate(r.Name, 55)),
                Styling.MarkupEscape(Truncate(r.Category, 20)),
                SeverityMarkup(r.Severity));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Total rules:", result.Rules.Count.ToString())}");
    }

    public static void RenderDisable(BpaRulesDisableResult result)
    {
        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"Rule '{Styling.MarkupEscape(result.RuleId)}' was already {(result.Disabled ? "disabled" : "enabled")} — no change."));
            return;
        }

        AnsiConsole.MarkupLine(result.Disabled
            ? $"Rule {Styling.Value(result.RuleId)} disabled for the current user."
            : $"Rule {Styling.Value(result.RuleId)} re-enabled for the current user.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Disabled rules:", result.DisabledRuleIds.Count.ToString())}");
    }

    public static void RenderIgnore(BpaRulesIgnoreResult result)
    {
        var verb = result.Ignored ? "ignored" : "no longer ignored";

        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"Rule '{Styling.MarkupEscape(result.RuleId)}' was already {(result.Ignored ? "ignored" : "not ignored")} — no change."));
            return;
        }

        AnsiConsole.MarkupLine($"Rule {Styling.Value(result.RuleId)} is now {verb} for {Styling.Value(result.ModelName)}.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Ignored rules:", result.RuleIds.Count.ToString())}");

        if (result.Saved is true or string)
            AnsiConsole.MarkupLine($"  {Styling.Success("Model saved.")}");
        else if (result.Staged == true)
            AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");
        else
            AnsiConsole.MarkupLine($"  {Styling.Muted("Not saved — re-run with --save to persist or --stage to stage.")}");

        if (result.Synced)
            AnsiConsole.MarkupLine($"  {Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}")}");
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine($"  {Styling.Warning(Styling.MarkupEscape(result.SyncWarning))}");
    }

    /// <summary>
    /// JSON projection for <c>bpa rules list</c>. Property names, order, and the conditional
    /// omission of empty fields are the output contract — keep stable (guarded by BpaJsonContractTests).
    /// </summary>
    internal static object ToListJson(BpaRulesListResult result)
        => new
        {
            rules = result.Rules.Select(ProjectRuleInfo),
            summary = result.Summary
        };

    internal static object ToDisableJson(BpaRulesDisableResult result)
        => new
        {
            ruleId = result.RuleId,
            disabled = result.Disabled,
            changed = result.Changed,
            disabledRuleIds = result.DisabledRuleIds
        };

    internal static object ToIgnoreJson(BpaRulesIgnoreResult result)
        => new
        {
            ruleId = result.RuleId,
            ignored = result.Ignored,
            changed = result.Changed,
            ruleIds = result.RuleIds,
            saved = result.Saved,
            staged = result.Staged,
            model = result.ModelName
        };

    private static Dictionary<string, object?> ProjectRuleInfo(BpaRuleInfo rule)
    {
        var json = new Dictionary<string, object?>
        {
            ["source"] = rule.Source,
            ["status"] = rule.Status,
            ["id"] = rule.Id,
            ["name"] = rule.Name,
            ["category"] = rule.Category,
            ["severity"] = (int)rule.Severity,
            ["severityLabel"] = rule.Severity.ToString(),
            ["scope"] = rule.Scope
        };

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

    private static string SeverityMarkup(BpaSeverity severity)
        => Styling.SeverityMarkup(severity switch
        {
            BpaSeverity.Error => "Error",
            BpaSeverity.Warning => "Warning",
            _ => "Info"
        });

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
}
