using Tomix.App.Models;
using Tomix.Core.Bpa;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

/// <param name="ActiveModel">
/// The active connection's model, if any. <c>unignore --user</c> reads its ignore list to warn
/// when the model still keeps the rule off; the check is best effort and never fails the command.
/// </param>
public sealed record BpaRulesDisableRequest(
    string RuleId, bool Disable, bool AllowUnknown = false, string? RulesFile = null, ModelReference? ActiveModel = null);

public sealed record BpaRulesDisableResult(
    string RuleId,
    bool Disabled,
    bool Changed,
    IReadOnlyList<string> DisabledRuleIds);

/// <summary>
/// <c>bpa rules ignore/unignore --user</c> (formerly <c>disable/enable</c>): switches a BPA rule
/// off or back on at the user level (see <see cref="BpaUserRuleState"/>).
/// </summary>
public sealed class BpaRulesDisableHandler
{
    private readonly BpaUserRuleState _state;
    private readonly string? _configDirectory;
    private readonly IReadOnlyList<IModelProvider> _providers;

    /// <param name="configDirectory">
    /// Where the user's <c>bpa-rules.json</c> lives, so its rules count as known IDs. Null checks
    /// against the bundled catalog only.
    /// </param>
    /// <param name="providers">Opens <see cref="BpaRulesDisableRequest.ActiveModel"/>; none skips that check.</param>
    public BpaRulesDisableHandler(
        BpaUserRuleState state, string? configDirectory = null, IEnumerable<IModelProvider>? providers = null)
    {
        _state = state;
        _configDirectory = configDirectory;
        _providers = providers?.ToList() ?? [];
    }

    public TomixResult<BpaRulesDisableResult> Handle(BpaRulesDisableRequest request)
        => Apply(request, modelIgnored: null);

    public async Task<TomixResult<BpaRulesDisableResult>> HandleAsync(
        BpaRulesDisableRequest request, CancellationToken cancellationToken)
    {
        var modelIgnored = !request.Disable && request.ActiveModel is { } model
            ? await ReadModelIgnoresAsync(model, cancellationToken).ConfigureAwait(false)
            : null;
        return Apply(request, modelIgnored);
    }

    private TomixResult<BpaRulesDisableResult> Apply(BpaRulesDisableRequest request, IReadOnlySet<string>? modelIgnored)
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

        return TomixResult<BpaRulesDisableResult>.Ok(
            result,
            diagnostics: request.Disable ? null : EnableWarnings(request, modelIgnored));
    }

    /// <summary>
    /// <c>unignore --user</c> only undoes <c>ignore --user</c>. Say when the rule still won't run:
    /// the model's ignore list keeps it off, or it isn't in the default ruleset at all.
    /// </summary>
    private List<TomixDiagnostic> EnableWarnings(BpaRulesDisableRequest request, IReadOnlySet<string>? modelIgnored)
    {
        var warnings = new List<TomixDiagnostic>();
        var id = request.RuleId;

        if (modelIgnored is not null && modelIgnored.Contains(id))
            warnings.Add(new TomixDiagnostic(
                "TOMIX_BPA_RULE_STILL_IGNORED_BY_MODEL",
                DiagnosticSeverity.Warning,
                $"You no longer ignore rule '{id}', but the model still does, so bpa run skips it for this model.",
                Hint: $"Stop ignoring it in the model: tx bpa rules unignore {id} {CommandArgument(request.ActiveModel!.Value)} --save"));

        if (BpaRuleLoader.OptInPresetFor(id) is { } preset && !InUserRulesFile(id))
        {
            var ruleset = preset == BpaRuleLoader.FullRuleset ? preset : $"{BpaRuleLoader.StandardRuleset},{preset}";
            warnings.Add(new TomixDiagnostic(
                "TOMIX_BPA_RULE_NOT_IN_RULESET",
                DiagnosticSeverity.Warning,
                $"Rule '{id}' isn't in the standard ruleset, so bpa run checks it only with --ruleset {ruleset}; unignoring it doesn't add it.",
                Hint: $"Run it with: tx bpa run --ruleset {ruleset}"));
        }

        return warnings;
    }

    private bool InUserRulesFile(string ruleId)
        => _configDirectory is not null
            && BpaRuleLoader.LoadFromFile(Path.Combine(_configDirectory, "bpa-rules.json"))
                .Any(r => r.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlySet<string>?> ReadModelIgnoresAsync(ModelReference model, CancellationToken cancellationToken)
    {
        try
        {
            if (_providers.ResolveSingleProvider(model) is not { } provider)
                return null;

            await using var session = await provider.OpenAsync(model, cancellationToken).ConfigureAwait(false);
            var snapshot = await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return BpaIgnoreStore.ReadRuleIds(snapshot.Properties);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A stale or unreadable active connection must not stop a rule from being enabled.
            return null;
        }
    }

    private static string CommandArgument(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
