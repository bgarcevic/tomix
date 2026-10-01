using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;

namespace Tomix.App.Bpa;

public sealed partial class BpaRuleLoader
{
    public const string StandardRuleset = "standard";
    public const string FullRuleset = "full";

    private const string BundledRulesResourceName = "Tomix.App.Bpa.Rules.bpa-rules.json";
    private const string MicrosoftRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/BPARules.json";
    private const string MicrosoftItalianRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Italian/BPARules.json";
    private const string MicrosoftJapaneseRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Japanese/BPARules.json";
    private const string MicrosoftSpanishRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Spanish/BPARules.json";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly IReadOnlyList<string> NamedRulesets =
    [
        StandardRuleset,
        FullRuleset,
        "microsoft",
        "microsoft-it",
        "microsoft-ja",
        "microsoft-es"
    ];

    private static readonly Lazy<IReadOnlyList<string>> KnownRulesetsLazy =
        new(() => [.. NamedRulesets, .. CategoryPresets(LoadBundledRules())]);

    /// <summary>
    /// Every <c>--ruleset</c> preset: the named rulesets plus one preset per default-off category
    /// the bundled catalog has rules for.
    /// </summary>
    public static IReadOnlyList<string> KnownRulesets => KnownRulesetsLazy.Value;

    /// <summary>
    /// Catalog categories that ship default-off (#233): neither <c>standard</c> nor <c>full</c>
    /// includes them, and each is opted into through its own <c>--ruleset</c> preset (the category
    /// name in kebab case), for example <c>--ruleset standard,localization</c>. A new category
    /// whose rules would bury real findings on most models — localization rules fire on every
    /// visible object of a single-culture model — belongs here rather than in <c>full</c>.
    /// </summary>
    public static IReadOnlySet<string> DefaultOffCategories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Localization" };

    /// <summary>
    /// The curated subset of the bundled catalog that makes up the <c>standard</c> ruleset.
    /// It also drives the deploy BPA gate, which blocks on error severity by default, so
    /// error-severity rules here must flag only a broken model (it fails to deploy, refresh, or
    /// query correctly). Noisy, heuristic, and style-opinion rules remain available through
    /// <c>--ruleset full</c>.
    /// </summary>
    private static readonly HashSet<string> CuratedRuleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Error prevention
        "DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN",
        "EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION",
        "RELATIONSHIP_COLUMNS_SAME_DATA_TYPE",
        "AVOID_THE_USERELATIONSHIP_FUNCTION_AND_RLS_AGAINST_THE_SAME_TABLE",
        "AVOID_INVALID_NAME_CHARACTERS",
        "AVOID_INVALID_DESCRIPTION_CHARACTERS",
        "SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS",
        // Performance
        "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS",
        "REDUCE_USAGE_OF_LONG-LENGTH_COLUMNS_WITH_HIGH_CARDINALITY",
        "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION",
        "AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY",
        "MODEL_SHOULD_HAVE_A_DATE_TABLE",
        "REMOVE_AUTO-DATE_TABLE",
        // DAX expressions
        "DAX_COLUMNS_FULLY_QUALIFIED",
        "DAX_MEASURES_UNQUALIFIED",
        "AVOID_DUPLICATE_MEASURES",
        "AVOID_USING_THE_IFERROR_FUNCTION",
        "FILTER_MEASURE_VALUES_BY_COLUMNS",
        "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED",
        "EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS",
        // Maintenance
        "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS",
        "CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS",
        // Formatting
        "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE",
        "PROVIDE_FORMAT_STRING_FOR_MEASURES",
        "HIDE_FOREIGN_KEYS",
        "MONTH_(AS_A_STRING)_MUST_BE_SORTED"
    };

    public static IReadOnlyList<BpaRule> LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return [];

        return ParseRules(File.ReadAllText(path));
    }

    public static IReadOnlyList<BpaRule> LoadFromJson(string json)
        => ParseRules(json);

    /// <summary>The entire embedded rule catalog, unfiltered.</summary>
    public static IReadOnlyList<BpaRule> LoadBundledCatalog()
        => LoadBundledRules();

    public static Task<IReadOnlyList<BpaRule>> LoadDefaultRulesAsync(CancellationToken cancellationToken)
        => LoadRulesetAsync(StandardRuleset, cancellationToken);

    public static Task<IReadOnlyList<BpaRule>> LoadRulesetAsync(
        string? ruleset,
        CancellationToken cancellationToken)
        => LoadRulesetAsync(ruleset, httpClient: null, cancellationToken);

    public static async Task<IReadOnlyList<BpaRule>> LoadRulesetAsync(
        string? ruleset,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var presets = SplitRulesets(ruleset);
        var rules = new List<BpaRule>();
        foreach (var preset in presets)
            rules.AddRange(await LoadPresetAsync(preset, httpClient, cancellationToken).ConfigureAwait(false));

        return rules;
    }

    /// <summary>
    /// The presets a <c>--ruleset</c> value names: a comma-separated list, trimmed, with empty
    /// entries dropped; no value at all means <c>standard</c>.
    /// </summary>
    public static IReadOnlyList<string> SplitRulesets(string? ruleset)
    {
        var presets = (ruleset ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return presets.Length == 0 ? [StandardRuleset] : presets;
    }

    /// <summary>
    /// Selects a bundled preset from <paramref name="catalog"/>: <c>standard</c> (the curated
    /// subset), <c>full</c> (everything outside the default-off categories), or a default-off
    /// category's own preset. Returns null for a preset that is not bundled.
    /// </summary>
    internal static IReadOnlyList<BpaRule>? SelectBundled(IReadOnlyList<BpaRule> catalog, string preset)
    {
        var key = preset.Trim();
        if (key.Equals("default", StringComparison.OrdinalIgnoreCase)
            || key.Equals(StandardRuleset, StringComparison.OrdinalIgnoreCase))
            return catalog.Where(rule => CuratedRuleIds.Contains(rule.Id) && !IsDefaultOff(rule)).ToList();

        if (key.Equals(FullRuleset, StringComparison.OrdinalIgnoreCase)
            || key.Equals("all", StringComparison.OrdinalIgnoreCase)
            || key.Equals("bundled", StringComparison.OrdinalIgnoreCase))
            return catalog.Where(rule => !IsDefaultOff(rule)).ToList();

        if (CategoryPresets(catalog).Contains(key, StringComparer.OrdinalIgnoreCase))
            return catalog.Where(rule => PresetName(rule.Category).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();

        return null;
    }

    /// <summary>
    /// The preset that brings a bundled rule into a run when <c>standard</c> does not: <c>full</c>,
    /// or its default-off category's preset. Null for a rule in <c>standard</c> or not bundled.
    /// </summary>
    public static string? OptInPresetFor(string ruleId)
    {
        var catalog = LoadBundledRules();
        var rule = catalog.FirstOrDefault(r => r.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null || SelectBundled(catalog, StandardRuleset)!.Contains(rule))
            return null;

        return IsDefaultOff(rule) ? PresetName(rule.Category) : FullRuleset;
    }

    /// <summary>One preset per default-off category that has at least one rule in the catalog.</summary>
    internal static IReadOnlyList<string> CategoryPresets(IReadOnlyList<BpaRule> catalog)
        => catalog
            .Where(IsDefaultOff)
            .Select(rule => PresetName(rule.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsDefaultOff(BpaRule rule) => DefaultOffCategories.Contains(rule.Category);

    private static string PresetName(string category)
        => string.Join('-', category.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static async Task<IReadOnlyList<BpaRule>> LoadPresetAsync(
        string preset,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        if (SelectBundled(LoadBundledRules(), preset) is { } bundled)
            return bundled;

        return await LoadFromSourceAsync(ResolveRemoteRuleset(preset), httpClient, cancellationToken).ConfigureAwait(false);
    }

    public static Task<IReadOnlyList<BpaRule>> LoadFromSourceAsync(
        string source,
        CancellationToken cancellationToken)
        => LoadFromSourceAsync(source, httpClient: null, cancellationToken);

    public static async Task<IReadOnlyList<BpaRule>> LoadFromSourceAsync(
        string source,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        if (TryCreateHttpUri(source, out var uri))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("tomix-cli");
            using var response = await (httpClient ?? SharedHttpClient)
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRules(json);
        }

        if (!File.Exists(source))
            throw new FileNotFoundException($"BPA rules file not found: {source}", source);

        return LoadFromFile(source);
    }

    private static IReadOnlyList<BpaRule> LoadBundledRules()
    {
        using var stream = typeof(BpaRuleLoader).Assembly
            .GetManifestResourceStream(BundledRulesResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded BPA rule catalog is unavailable: {BundledRulesResourceName}");
        using var reader = new StreamReader(stream);
        return ParseRules(reader.ReadToEnd());
    }

    private static IReadOnlyList<BpaRule> ParseRules(string json)
    {
        var raw = JsonSerializer.Deserialize(json, RuleJsonContext.Default.ListJsonRule);
        if (raw is null)
            return [];

        return raw.Select(rule => new BpaRule(
            rule.Id ?? "",
            rule.Name ?? "",
            rule.Category ?? "",
            MapSeverity(rule.Severity),
            ParseScope(rule.Scope ?? ""),
            rule.Description,
            rule.Expression,
            rule.FixExpression,
            rule.CompatibilityLevel)).ToList();
    }

    private static RuleSeverity MapSeverity(int severity) => severity switch
    {
        1 => RuleSeverity.Info,
        2 => RuleSeverity.Warning,
        3 => RuleSeverity.Error,
        _ => RuleSeverity.Info
    };

    private static IReadOnlyList<string> ParseScope(string scope)
        => scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim())
            .ToList();

    private static string ResolveRemoteRuleset(string preset)
    {
        var key = preset.Trim();
        return key.ToLowerInvariant() switch
        {
            "microsoft" or "microsoft-en" => MicrosoftRulesUrl,
            "microsoft-it" or "microsoft-italian" => MicrosoftItalianRulesUrl,
            "microsoft-ja" or "microsoft-japanese" => MicrosoftJapaneseRulesUrl,
            "microsoft-es" or "microsoft-spanish" => MicrosoftSpanishRulesUrl,
            _ => throw new ArgumentException(
                $"Unknown BPA ruleset '{key}'. Known rulesets: {string.Join(", ", KnownRulesets)}. Use --rules for a custom file or URL.")
        };
    }

    private static bool TryCreateHttpUri(string source, out Uri? uri)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;

        uri = null;
        return false;
    }

    private sealed class JsonRule
    {
        [JsonPropertyName("ID")] public string? Id { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Category")] public string? Category { get; set; }
        [JsonPropertyName("Description")] public string? Description { get; set; }
        [JsonPropertyName("Severity")] public int Severity { get; set; }
        [JsonPropertyName("Scope")] public string? Scope { get; set; }
        [JsonPropertyName("Expression")] public string? Expression { get; set; }
        [JsonPropertyName("FixExpression")] public string? FixExpression { get; set; }
        [JsonPropertyName("CompatibilityLevel")] public int CompatibilityLevel { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<JsonRule>))]
    private sealed partial class RuleJsonContext : JsonSerializerContext;
}
