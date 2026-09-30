using System.Text.Json;
using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.Core.Bpa;
using Tomix.Core.Configuration;
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
    bool Enabled,
    bool Disabled = false,
    bool Ignored = false,
    IReadOnlyList<string>? Overrides = null);

public sealed class BpaRulesListHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly BpaUserRuleState _userRules;
    private readonly HttpClient? _httpClient;
    private readonly string? _configDirectory;
    private readonly Func<string, string?> _environment;

    /// <param name="configDirectory">
    /// Where the user's <c>bpa-rules.json</c> and the <c>bpa.rules</c> key live; their rules are
    /// listed as <c>bpa run</c> loads them. Null leaves the user file and the configured rule
    /// sources (including <c>TOMIX_BPA_RULES</c>) out.
    /// </param>
    /// <param name="environment">Reads <c>TOMIX_BPA_RULES</c>; defaults to the process environment.</param>
    public BpaRulesListHandler(
        IEnumerable<IModelProvider>? providers,
        BpaUserRuleState userRules,
        HttpClient? httpClient = null,
        string? configDirectory = null,
        Func<string, string?>? environment = null)
    {
        _providers = providers?.ToList() ?? [];
        _userRules = userRules;
        _httpClient = httpClient;
        _configDirectory = configDirectory;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public async Task<TomixResult<BpaRulesListResult>> HandleAsync(
        BpaRulesListRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BpaRuleCollection> rules;
        var sourceDiagnostics = new List<string>();
        try
        {
            rules = await LoadRulesAsync(request, sourceDiagnostics, cancellationToken).ConfigureAwait(false);
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
            var diagnostics = new List<string>(sourceDiagnostics);
            var loaded = new List<BpaRuleCollection>(rules);

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
                loaded.AddRange(model.Collections);
                diagnostics.AddRange(model.Diagnostics);
            }

            // Status names one level for compatibility — a user-level disable wins because it applies
            // to every model — while Disabled and Ignored report both, since turning one level back
            // on leaves the other in force.
            BpaRuleInfo Info(BpaRuleCollection source, BpaRule rule, IReadOnlyList<string>? overrides)
            {
                var isDisabled = disabled.Contains(rule.Id);
                var isIgnored = ignored.Contains(rule.Id);
                var status = isDisabled ? "disabled"
                    : isIgnored ? "ignored"
                    : "active";
                return new BpaRuleInfo(
                    source.DisplayName,
                    Status: status,
                    rule.Id,
                    rule.Name,
                    rule.Category,
                    rule.Severity,
                    string.Join(", ", rule.Scope),
                    rule.Description,
                    rule.Expression,
                    rule.FixExpression,
                    Enabled: status == "active",
                    Disabled: isDisabled,
                    Ignored: isIgnored,
                    Overrides: overrides);
            }

            // `show` returns every source's copy of the rule, so an override can be compared with
            // what it replaced.
            if (!string.IsNullOrWhiteSpace(request.RuleId))
                return FindRule(
                    loaded.SelectMany(c => c.Rules.Select(r => Info(c, r, overrides: null))).ToList(),
                    request.RuleId,
                    diagnostics);

            // `list` shows each rule once, from the source that wins as in `bpa run`, and names the
            // sources it overrides (for example `full` over `standard`, or a team file over both).
            var allRules = BpaRuleResolver.ResolveEffective(loaded)
                .Select(e => Info(
                    e.Source,
                    e.Rule,
                    e.Overrides.Count > 0 ? e.Overrides.Select(o => o.DisplayName).Distinct().ToList() : null))
                .ToList();

            // --ignored covers both levels (the status names which); the hidden --disabled keeps
            // listing only the user level for scripts written before `ignore --user`.
            var filteredRules = (request.DisabledOnly, request.IgnoredOnly, request.All) switch
            {
                (true, false, _) => allRules.Where(r => r.Disabled).ToList(),
                (_, true, _) => allRules.Where(r => r.Disabled || r.Ignored).ToList(),
                (_, _, true) => allRules,
                _ => allRules.Where(r => r.Enabled).ToList()
            };

            var result = new BpaRulesListResult(
                filteredRules,
                Summarize(allRules),
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

        var summary = Summarize(matches);
        return TomixResult<BpaRulesListResult>.Ok(
            new BpaRulesListResult(matches, summary, diagnostics.Count > 0 ? diagnostics : null));
    }

    /// <summary>
    /// Disabled and Ignored count every rule off at that level, so a rule off at both levels is
    /// counted in each, and Active + Disabled + Ignored can exceed Total.
    /// </summary>
    private static BpaRulesSummary Summarize(IReadOnlyList<BpaRuleInfo> rules)
        => new(
            Total: rules.Count,
            Active: rules.Count(r => r.Enabled),
            Disabled: rules.Count(r => r.Disabled),
            Ignored: rules.Count(r => r.Ignored));

    /// <summary>
    /// The listed sources in precedence order, as <c>bpa run</c> loads them: each preset, the
    /// user file, <c>bpa.rules</c>, <c>TOMIX_BPA_RULES</c>, then the selected <c>--rules-file</c>
    /// (last, so the file being authored shows as it is).
    /// </summary>
    private async Task<IReadOnlyList<BpaRuleCollection>> LoadRulesAsync(
        BpaRulesListRequest request,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClient;
        var configDirectory = _configDirectory;
        var rules = new List<BpaRuleCollection>();

        if (!request.NoDefaults)
        {
            foreach (var preset in BpaRuleLoader.SplitRulesets(request.Ruleset))
                rules.Add(new BpaRuleCollection(
                    BpaRuleSourceKind.Machine,
                    preset,
                    await BpaRuleLoader.LoadRulesetAsync(preset, httpClient, cancellationToken).ConfigureAwait(false)));
        }

        // The user file is listed unless --rules-file already selected it.
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            var userFile = Path.GetFullPath(Path.Combine(configDirectory, BpaRulesFile.UserFileName));
            var selected = request.RulesFile is { Length: > 0 } file && !file.Contains("://", StringComparison.Ordinal)
                ? Path.GetFullPath(file)
                : null;
            if (File.Exists(userFile) && !string.Equals(userFile, selected, StringComparison.OrdinalIgnoreCase))
                rules.Add(new BpaRuleCollection(
                    BpaRuleSourceKind.User, "user", BpaRuleLoader.LoadFromFile(userFile), BpaRuleOrigin.UserFile));

            // The configured rule sources bpa run also loads (#233), listed under the setting that
            // names them. One that can't be loaded is reported, not fatal: the listing still
            // answers for every other source.
            foreach (var entry in BpaRuleSources.Resolve(configDirectory, _environment, optionFiles: null))
            {
                var source = entry.Origin == BpaRuleOrigin.Config ? ConfigKeys.BpaRules : BpaRuleSources.EnvironmentVariable;
                try
                {
                    rules.Add(new BpaRuleCollection(
                        BpaRuleSourceKind.User,
                        source,
                        await BpaRuleSources.LoadEntryAsync(entry, httpClient, cancellationToken).ConfigureAwait(false),
                        entry.Origin));
                }
                catch (Exception ex) when (ex is FileNotFoundException or HttpRequestException or JsonException or InvalidOperationException)
                {
                    diagnostics.Add(ex.Message);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.RulesFile))
        {
            rules.Add(new BpaRuleCollection(
                BpaRuleSourceKind.User,
                "custom",
                await BpaRuleLoader.LoadFromSourceAsync(request.RulesFile, httpClient, cancellationToken).ConfigureAwait(false),
                BpaRuleOrigin.Option));
        }

        return rules;
    }
}
