using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesDisableRequest(
    string RuleId, bool Disable, bool AllowUnknown = false, string? RulesFile = null);

public sealed record BpaRulesDisableResult(
    string RuleId,
    bool Disabled,
    bool Changed,
    IReadOnlyList<string> DisabledRuleIds);

/// <summary>Enables/disables a BPA rule at the user level (see <see cref="BpaUserRuleState"/>).</summary>
public sealed class BpaRulesDisableHandler
{
    private readonly BpaUserRuleState _state;
    private readonly string? _configDirectory;

    /// <param name="configDirectory">
    /// Where the user's <c>bpa-rules.json</c> lives, so its rules count as known IDs. Null checks
    /// against the bundled catalog only.
    /// </param>
    public BpaRulesDisableHandler(BpaUserRuleState state, string? configDirectory = null)
    {
        _state = state;
        _configDirectory = configDirectory;
    }

    public TomixResult<BpaRulesDisableResult> Handle(BpaRulesDisableRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return TomixResult<BpaRulesDisableResult>.Fail(
                "TOMIX_BPA_RULE_ID_REQUIRED", "A rule id is required.", exitCode: 2);

        // Only disabling is checked: enabling must stay possible for an ID that no longer exists.
        if (request.Disable && !request.AllowUnknown
            && BpaKnownRules.Load(_configDirectory, request.RulesFile).Check<BpaRulesDisableResult>(request.RuleId) is { } unknown)
            return unknown;

        var changed = request.Disable ? _state.Disable(request.RuleId) : _state.Enable(request.RuleId);

        var result = new BpaRulesDisableResult(
            request.RuleId,
            Disabled: request.Disable,
            Changed: changed,
            DisabledRuleIds: _state.GetDisabled().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());

        return TomixResult<BpaRulesDisableResult>.Ok(result);
    }
}
