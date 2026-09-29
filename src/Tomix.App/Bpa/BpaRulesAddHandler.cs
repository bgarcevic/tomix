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

    private readonly string _configDirectory;

    public BpaRulesAddHandler(string configDirectory) => _configDirectory = configDirectory;

    public TomixResult<BpaRulesFileResult> Handle(BpaRulesAddRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        if (BpaRulesFile.TryOpen(_configDirectory, request.RulesFile, createIfMissing: true, out var file) is { } failed)
            return failed;

        var ruleId = request.RuleId.Trim();
        if (file.TryAdd(ruleId, request.Fields, out var rule) is { } invalid)
            return invalid;

        file.Save();

        return TomixResult<BpaRulesFileResult>.Ok(new BpaRulesFileResult(
            "add", file.Path, Changed: true, file.Count, ruleId, file.Describe(rule)));
    }
}
