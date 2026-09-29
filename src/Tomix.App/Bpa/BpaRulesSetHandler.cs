using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesSetRequest(
    string RuleId,
    BpaRuleFields Fields,
    string? RulesFile = null);

/// <summary>
/// Changes fields of a rule in a rules file (<c>bpa rules set</c>). Only the rule's own file is
/// edited: a built-in or model rule is overridden by adding a rule with the same ID instead.
/// Setting the values a rule already has reports no change and leaves the file untouched.
/// </summary>
public sealed class BpaRulesSetHandler
{
    private readonly string _configDirectory;

    public BpaRulesSetHandler(string configDirectory) => _configDirectory = configDirectory;

    public TomixResult<BpaRulesFileResult> Handle(BpaRulesSetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        if (request.Fields.IsEmpty)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_FIELD_REQUIRED",
                "Nothing to change.",
                exitCode: 2,
                hint: "Pass at least one of --name, --category, --severity, --scope, --expression, --description, --fix-expression.");

        if (BpaRulesFile.TryOpen(_configDirectory, request.RulesFile, createIfMissing: false, out var file) is { } failed)
            return failed;

        var ruleId = request.RuleId.Trim();
        if (file.Find(ruleId) is not { } rule)
            return BpaRulesRemoveHandler.NotInFile(ruleId, file.Path);

        if (BpaRulesFile.TryApply(rule, request.Fields, out var changed) is { } invalid)
            return invalid;

        if (changed.Count > 0)
            file.Save();

        return TomixResult<BpaRulesFileResult>.Ok(new BpaRulesFileResult(
            "set", file.Path, Changed: changed.Count > 0, file.Count, ruleId, file.Describe(rule), changed));
    }
}
