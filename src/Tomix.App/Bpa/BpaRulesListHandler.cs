using System.Text.Json;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesListRequest(
    ModelReference? Model = null,
    bool All = false,
    string? RulesFile = null,
    string? Ruleset = null,
    bool NoDefaults = false,
    bool IgnoredOnly = false,
    bool DisabledOnly = false);

public sealed record BpaRulesListResult(
    IReadOnlyList<BpaRuleInfo> Rules,
    BpaRulesSummary Summary);

public sealed record BpaRulesSummary(
    int Total,
    int Active,
    int Disabled,
    int Ignored);

public sealed record BpaRuleInfo(
    string Source,
    string Status,
    string Id,
    string Name,
    string Category,
    BpaSeverity Severity,
    string Scope,
    string? Description,
    string? Expression,
    string? FixExpression,
    bool Enabled);

public sealed class BpaRulesListHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly BpaUserRuleState _userRules;

    public BpaRulesListHandler(IEnumerable<IModelProvider>? providers, BpaUserRuleState userRules)
    {
        _providers = providers?.ToList() ?? [];
        _userRules = userRules;
    }

    public async Task<TomixResult<BpaRulesListResult>> HandleAsync(
        BpaRulesListRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LoadedRule> rules;
        try
        {
            rules = await LoadRulesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or HttpRequestException or JsonException)
        {
            return TomixResult<BpaRulesListResult>.Fail(
                "TOMIX_BPA_RULES_LOAD_FAILED",
                ex.Message,
                exitCode: 2);
        }

        // When a model is supplied, rules listed in its model-level ignore annotation are disabled.
        var disabled = await ReadDisabledRuleIdsAsync(request.Model, cancellationToken).ConfigureAwait(false);

        var allRules = rules.Select(r =>
        {
            var isDisabled = disabled.Contains(r.Rule.Id);
            return new BpaRuleInfo(
                r.Source,
                Status: isDisabled ? "disabled" : "active",
                r.Rule.Id,
                r.Rule.Name,
                r.Rule.Category,
                r.Rule.Severity,
                string.Join(", ", r.Rule.Scope),
                r.Rule.Description,
                r.Rule.Expression,
                r.Rule.FixExpression,
                Enabled: !isDisabled);
        }).ToList();

        var filteredRules = (request.DisabledOnly, request.IgnoredOnly, request.All) switch
        {
            (true, _, _) => allRules.Where(r => !r.Enabled).ToList(),
            (_, true, _) => allRules.Where(r => !r.Enabled).ToList(),
            (_, _, true) => allRules,
            _ => allRules.Where(r => r.Enabled).ToList()
        };

        var disabledCount = allRules.Count(r => !r.Enabled);
        var result = new BpaRulesListResult(
            filteredRules,
            new BpaRulesSummary(
                Total: allRules.Count,
                Active: allRules.Count - disabledCount,
                Disabled: disabledCount,
                Ignored: 0));

        return TomixResult<BpaRulesListResult>.Ok(result);
    }

    private async Task<IReadOnlySet<string>> ReadDisabledRuleIdsAsync(
        ModelReference? model,
        CancellationToken cancellationToken)
    {
        // User-level disables apply regardless of model; model-level ignores add to them.
        var disabled = new HashSet<string>(_userRules.GetDisabled(), StringComparer.OrdinalIgnoreCase);

        if (model is null)
            return disabled;

        var provider = _providers.ResolveSingle(model);
        if (provider is null)
            return disabled;

        await using var session = await provider.OpenAsync(model, cancellationToken);
        var snapshot = await session.GetSnapshotAsync(cancellationToken);
        disabled.UnionWith(BpaIgnoreStore.ReadRuleIds(snapshot.Properties));
        return disabled;
    }

    private static async Task<IReadOnlyList<LoadedRule>> LoadRulesAsync(
        BpaRulesListRequest request,
        CancellationToken cancellationToken)
    {
        var rules = new List<LoadedRule>();

        if (!request.NoDefaults)
        {
            var source = string.IsNullOrWhiteSpace(request.Ruleset)
                ? BpaRuleLoader.StandardRuleset
                : request.Ruleset;
            rules.AddRange((await BpaRuleLoader.LoadRulesetAsync(request.Ruleset, cancellationToken).ConfigureAwait(false))
                .Select(rule => new LoadedRule(source, rule)));
        }

        if (!string.IsNullOrWhiteSpace(request.RulesFile))
        {
            rules.AddRange((await BpaRuleLoader.LoadFromSourceAsync(request.RulesFile, cancellationToken).ConfigureAwait(false))
                .Select(rule => new LoadedRule("custom", rule)));
        }

        return rules;
    }

    private sealed record LoadedRule(string Source, BpaRule Rule);
}
