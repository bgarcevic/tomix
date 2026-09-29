using System.Text.Json.Nodes;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesAddRequest(
    string RuleId,
    BpaRuleFields Fields,
    string? RulesFile = null);

/// <summary>
/// Adds a rule to a rules file (<c>bpa rules add</c>), creating the file when it does not exist.
/// Name, scope, and expression are required; category defaults to <c>Custom</c> and severity to
/// warning. An ID the file already has fails, so an add never silently overwrites a rule.
/// </summary>
public sealed class BpaRulesAddHandler
{
    public const string DefaultCategory = "Custom";

    private static readonly string[] FieldOrder =
        ["ID", "Name", "Category", "Description", "Severity", "Scope", "Expression", "FixExpression", "CompatibilityLevel"];

    private readonly string _configDirectory;

    public BpaRulesAddHandler(string configDirectory) => _configDirectory = configDirectory;

    public TomixResult<BpaRulesFileResult> Handle(BpaRulesAddRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        var missing = new[]
            {
                ("--name", request.Fields.Name),
                ("--scope", request.Fields.Scope),
                ("--expression", request.Fields.Expression)
            }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2))
            .Select(f => f.Item1)
            .ToList();
        if (missing.Count > 0)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_FIELD_REQUIRED",
                $"A new rule needs {string.Join(", ", missing)}.",
                exitCode: 2);

        if (BpaRulesFile.TryOpen(_configDirectory, request.RulesFile, createIfMissing: true, out var file) is { } failed)
            return failed;

        var ruleId = request.RuleId.Trim();
        if (file.Find(ruleId) is not null)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_EXISTS",
                $"Rule '{ruleId}' already exists in {file.Path}.",
                exitCode: 2,
                hint: $"Change it with 'tx bpa rules set {ruleId}', or remove it first.");

        var draft = new JsonObject
        {
            ["ID"] = ruleId,
            ["Category"] = DefaultCategory,
            ["Severity"] = 2,
            ["CompatibilityLevel"] = 1200
        };
        if (BpaRulesFile.TryApply(draft, request.Fields, out _) is { } invalid)
            return invalid;

        // Write the fields in the order the bundled catalog uses.
        var rule = new JsonObject();
        foreach (var field in FieldOrder)
            if (draft[field] is { } value)
                rule[field] = value.DeepClone();

        file.Add(rule);
        file.Save();

        return TomixResult<BpaRulesFileResult>.Ok(new BpaRulesFileResult(
            "add", file.Path, Changed: true, file.Count, ruleId, file.Describe(rule)));
    }
}
