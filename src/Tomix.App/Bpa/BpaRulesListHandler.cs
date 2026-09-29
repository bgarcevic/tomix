using System.Text.Json;
using Tomix.App.Diagnostics;
using Tomix.App.Models;
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
    bool DisabledOnly = false,
    string? RuleId = null);

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
    private readonly string? _configDirectory;

    /// <param name="configDirectory">
    /// Where the user's <c>bpa-rules.json</c> lives; its rules are listed with source <c>user</c>,
    /// as <c>bpa run</c> loads them. Null leaves the user file out.
    /// </param>
    public BpaRulesListHandler(
        IEnumerable<IModelProvider>? providers,
        BpaUserRuleState userRules,
        HttpClient? httpClient = null,
        string? configDirectory = null)
    {
        _providers = providers?.ToList() ?? [];
        _userRules = userRules;
        _httpClient = httpClient;
        _configDirectory = configDirectory;
    }

    public async Task<TomixResult<BpaRulesListResult>> HandleAsync(
        BpaRulesListRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LoadedRule> rules;
        try
        {
            rules = await LoadRulesAsync(request, _httpClient, _configDirectory, cancellationToken).ConfigureAwait(false);
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
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diagnostics = new List<string>();
            var loaded = new List<LoadedRule>(rules);

            if (request.Model is not null && _providers.ResolveSingleProvider(request.Model) is { } provider)
            {
                await using var session = await provider.OpenAsync(request.Model, cancellationToken);
                var snapshot = await session.GetSnapshotAsync(cancellationToken);
                ignored.UnionWith(BpaIgnoreStore.ReadRuleIds(snapshot.Properties));

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

            // A user-level disable wins over the model's ignore list: it applies to every model.
            var allRules = loaded.Select(r =>
            {
                var status = disabled.Contains(r.Rule.Id) ? "disabled"
                    : ignored.Contains(r.Rule.Id) ? "ignored"
                    : "active";
                return new BpaRuleInfo(
                    r.Source,
                    Status: status,
                    r.Rule.Id,
                    r.Rule.Name,
                    r.Rule.Category,
                    r.Rule.Severity,
                    string.Join(", ", r.Rule.Scope),
                    r.Rule.Description,
                    r.Rule.Expression,
                    r.Rule.FixExpression,
                    Enabled: status == "active");
            }).ToList();

            if (!string.IsNullOrWhiteSpace(request.RuleId))
                return FindRule(allRules, request.RuleId, diagnostics);

            var filteredRules = (request.DisabledOnly, request.IgnoredOnly, request.All) switch
            {
                (true, true, _) => allRules.Where(r => !r.Enabled).ToList(),
                (true, _, _) => allRules.Where(r => r.Status == "disabled").ToList(),
                (_, true, _) => allRules.Where(r => r.Status == "ignored").ToList(),
                (_, _, true) => allRules,
                _ => allRules.Where(r => r.Enabled).ToList()
            };

            var disabledCount = allRules.Count(r => r.Status == "disabled");
            var ignoredCount = allRules.Count(r => r.Status == "ignored");
            var result = new BpaRulesListResult(
                filteredRules,
                new BpaRulesSummary(
                    Total: allRules.Count,
                    Active: allRules.Count - disabledCount - ignoredCount,
                    Disabled: disabledCount,
                    Ignored: ignoredCount),
                Diagnostics: diagnostics.Count > 0 ? diagnostics : null);

            return TomixResult<BpaRulesListResult>.Ok(result);
        });
    }

    /// <summary>
    /// The single-rule lookup behind <c>bpa rules show</c>. The same ID can come from more than one
    /// source (a model rule overriding the ruleset), so every match is returned. An unknown ID fails
    /// with up to three IDs that contain the input as a hint.
    /// </summary>
    private static TomixResult<BpaRulesListResult> FindRule(
        IReadOnlyList<BpaRuleInfo> allRules, string ruleId, IReadOnlyList<string> diagnostics)
    {
        var matches = allRules.Where(r => r.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            var near = allRules
                .Select(r => r.Id)
                .Where(id => id.Contains(ruleId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            return TomixResult<BpaRulesListResult>.Fail(
                "TOMIX_BPA_RULE_NOT_FOUND",
                $"No BPA rule with ID '{ruleId}'.",
                exitCode: 2,
                hint: near.Count > 0
                    ? $"Did you mean: {string.Join(", ", near)}?"
                    : "Run 'tx bpa rules list --all' to see every rule ID.");
        }

        var summary = new BpaRulesSummary(
            Total: matches.Count,
            Active: matches.Count(r => r.Status == "active"),
            Disabled: matches.Count(r => r.Status == "disabled"),
            Ignored: matches.Count(r => r.Status == "ignored"));
        return TomixResult<BpaRulesListResult>.Ok(
            new BpaRulesListResult(matches, summary, diagnostics.Count > 0 ? diagnostics : null));
    }

    private static async Task<IReadOnlyList<LoadedRule>> LoadRulesAsync(
        BpaRulesListRequest request,
        HttpClient? httpClient,
        string? configDirectory,
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

        // The user file is listed unless --rules-file already selected it.
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            var userFile = Path.GetFullPath(Path.Combine(configDirectory, BpaRulesFile.UserFileName));
            var selected = request.RulesFile is { Length: > 0 } file && !file.Contains("://", StringComparison.Ordinal)
                ? Path.GetFullPath(file)
                : null;
            if (File.Exists(userFile) && !string.Equals(userFile, selected, StringComparison.OrdinalIgnoreCase))
                rules.AddRange(BpaRuleLoader.LoadFromFile(userFile).Select(rule => new LoadedRule("user", rule)));
        }

        return rules;
    }

    private sealed record LoadedRule(string Source, BpaRule Rule);
}
