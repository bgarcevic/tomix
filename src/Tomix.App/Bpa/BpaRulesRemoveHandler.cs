using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesRemoveRequest(
    string RuleId,
    string? RulesFile = null);

/// <summary>
/// Deletes a rule from a rules file (<c>bpa rules remove</c>). Built-in rules are not in a file
/// tx edits; turn those off with <c>bpa rules ignore</c> (with or without <c>--user</c>).
/// </summary>
public sealed class BpaRulesRemoveHandler
{
    private readonly string _configDirectory;

    public BpaRulesRemoveHandler(string configDirectory) => _configDirectory = configDirectory;

    public TomixResult<BpaRulesFileResult> Handle(BpaRulesRemoveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        if (BpaRulesFile.TryOpen(_configDirectory, request.RulesFile, createIfMissing: false, out var file) is { } failed)
            return failed;

        var ruleId = request.RuleId.Trim();
        if (file.Find(ruleId) is not { } rule)
            return NotInFile(ruleId, file.Path);

        file.Remove(rule);
        file.Save();

        return TomixResult<BpaRulesFileResult>.Ok(new BpaRulesFileResult(
            "remove", file.Path, Changed: true, file.Count, ruleId));
    }

    /// <summary>
    /// <c>TOMIX_BPA_RULE_NOT_FOUND</c> for an ID the edited file does not define. A built-in ID
    /// gets a hint toward the commands that turn built-in rules off.
    /// </summary>
    internal static TomixResult<BpaRulesFileResult> NotInFile(string ruleId, string path)
    {
        var builtIn = BpaRuleLoader.LoadBundledCatalog()
            .Any(r => r.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        return TomixResult<BpaRulesFileResult>.Fail(
            "TOMIX_BPA_RULE_NOT_FOUND",
            $"No rule with ID '{ruleId}' in {path}.",
            exitCode: 2,
            hint: builtIn
                ? $"'{ruleId}' is a built-in rule. Turn it off with 'tx bpa rules ignore {ruleId} --user', or override it with 'tx bpa rules add --id {ruleId} ...'."
                : "Run 'tx bpa rules list --all' to see every rule and its source.");
    }
}
