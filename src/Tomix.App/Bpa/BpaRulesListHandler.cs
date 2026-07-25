using System.Text.Json;
using Tomix.App.Diagnostics;
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
    BpaRulesSummary Summary,
    IReadOnlyList<string>? Diagnostics = null);

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
    private readonly HttpClient? _httpClient;

    public BpaRulesListHandler(
        IEnumerable<IModelProvider>? providers,
        BpaUserRuleState userRules,
        HttpClient? httpClient = null)
    {
        _providers = providers?.ToList() ?? [];
        _userRules = userRules;
        _httpClient = httpClient;
    }

    public async Task<TomixResult<BpaRulesListResult>> HandleAsync(
        BpaRulesListRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LoadedRule> rules;
        try
        {
            rules = await LoadRulesAsync(request, _httpClient, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or HttpRequestException or JsonException)
        {
            return TomixResult<BpaRulesListResult>.Fail(
                "TOMIX_BPA_RULES_LOAD_FAILED",
                ex.Message,
                exitCode: 2);
        }

        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            // When a model is supplied, rules listed in its model-level ignore annotation are
            // disabled, and the model's own rule sources (embedded + external files) are listed
            // alongside the ruleset. Remote external files are never fetched here; the loader
            // reports them as skipped.
            var disabled = new HashSet<string>(_userRules.GetDisabled(), StringComparer.OrdinalIgnoreCase);
            var diagnostics = new List<string>();
            var loaded = new List<LoadedRule>(rules);

            if (request.Model is not null && _providers.ResolveSingle(request.Model) is { } provider)
            {
                await using var session = await provider.OpenAsync(request.Model, cancellationToken);
                var snapshot = await session.GetSnapshotAsync(cancellationToken);
                disabled.UnionWith(BpaIgnoreStore.ReadRuleIds(snapshot.Properties));

                var model = await BpaModelRuleLoader.LoadAsync(
                    snapshot.Properties,
                    BpaModelRuleLoader.ResolveBaseDirectory(session, request.Model),
                    allowExternal: false,
                    BpaRuleHintContext.List,
                    _httpClient,
                    cancellationToken).ConfigureAwait(false);
                loaded.AddRange(model.Collections.SelectMany(
                    c => c.Rules.Select(r => new LoadedRule(c.DisplayName, r))));
                diagnostics.AddRange(model.Diagnostics);
            }

            var allRules = loaded.Select(r =>
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
                    Ignored: 0),
                Diagnostics: diagnostics.Count > 0 ? diagnostics : null);

            return TomixResult<BpaRulesListResult>.Ok(result);
        });
    }

    private static async Task<IReadOnlyList<LoadedRule>> LoadRulesAsync(
        BpaRulesListRequest request,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var rules = new List<LoadedRule>();

        if (!request.NoDefaults)
        {
            var source = string.IsNullOrWhiteSpace(request.Ruleset)
                ? BpaRuleLoader.StandardRuleset
                : request.Ruleset;
            rules.AddRange((await BpaRuleLoader
                    .LoadRulesetAsync(request.Ruleset, httpClient, cancellationToken)
                    .ConfigureAwait(false))
                .Select(rule => new LoadedRule(source, rule)));
        }

        if (!string.IsNullOrWhiteSpace(request.RulesFile))
        {
            rules.AddRange((await BpaRuleLoader
                    .LoadFromSourceAsync(request.RulesFile, httpClient, cancellationToken)
                    .ConfigureAwait(false))
                .Select(rule => new LoadedRule("custom", rule)));
        }

        return rules;
    }

    private sealed record LoadedRule(string Source, BpaRule Rule);
}
