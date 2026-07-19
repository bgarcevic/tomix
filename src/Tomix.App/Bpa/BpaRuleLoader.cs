using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

public sealed class BpaRuleLoader
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

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<string> KnownRulesets { get; } =
    [
        StandardRuleset,
        FullRuleset,
        "microsoft",
        "microsoft-it",
        "microsoft-ja",
        "microsoft-es"
    ];

    /// <summary>
    /// The curated subset of the bundled catalog that makes up the <c>standard</c> ruleset.
    /// Style-opinion and advisory rules remain available through <c>--ruleset full</c>.
    /// </summary>
    private static readonly HashSet<string> CuratedRuleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN",
        "EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION",
        "RELATIONSHIP_COLUMNS_SAME_DATA_TYPE",
        "AVOID_THE_USERELATIONSHIP_FUNCTION_AND_RLS_AGAINST_THE_SAME_TABLE",
        "AVOID_INVALID_NAME_CHARACTERS",
        "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE",
        "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS",
        "SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS",
        "AVOID_FLOATING_POINT_DATA_TYPES",
        "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS",
        "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION",
        "AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY",
        "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS",
        "MODEL_SHOULD_HAVE_A_DATE_TABLE",
        "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE",
        "REMOVE_AUTO-DATE_TABLE",
        "REDUCE_USAGE_OF_LONG-LENGTH_COLUMNS_WITH_HIGH_CARDINALITY",
        "DAX_COLUMNS_FULLY_QUALIFIED",
        "DAX_MEASURES_UNQUALIFIED",
        "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION",
        "AVOID_USING_THE_IFERROR_FUNCTION",
        "FILTER_MEASURE_VALUES_BY_COLUMNS",
        "EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS",
        "PROVIDE_FORMAT_STRING_FOR_MEASURES",
        "HIDE_FOREIGN_KEYS",
        "NUMERIC_COLUMN_SUMMARIZE_BY",
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
        var source = ResolveRulesetSource(ruleset);
        if (source == StandardRuleset)
            return LoadBundledRules().Where(rule => CuratedRuleIds.Contains(rule.Id)).ToList();

        if (source == FullRuleset)
            return LoadBundledRules();

        return await LoadFromSourceAsync(source, httpClient, cancellationToken).ConfigureAwait(false);
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
        var raw = JsonSerializer.Deserialize<List<JsonRule>>(json, Options);
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

    private static BpaSeverity MapSeverity(int severity) => severity switch
    {
        1 => BpaSeverity.Info,
        2 => BpaSeverity.Warning,
        3 => BpaSeverity.Error,
        _ => BpaSeverity.Info
    };

    private static IReadOnlyList<string> ParseScope(string scope)
        => scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim())
            .ToList();

    private static string ResolveRulesetSource(string? ruleset)
    {
        var key = string.IsNullOrWhiteSpace(ruleset) ? StandardRuleset : ruleset.Trim();

        if (key.Equals("default", StringComparison.OrdinalIgnoreCase)
            || key.Equals(StandardRuleset, StringComparison.OrdinalIgnoreCase))
            return StandardRuleset;

        if (key.Equals(FullRuleset, StringComparison.OrdinalIgnoreCase)
            || key.Equals("all", StringComparison.OrdinalIgnoreCase)
            || key.Equals("bundled", StringComparison.OrdinalIgnoreCase))
            return FullRuleset;

        return key.ToLowerInvariant() switch
        {
            "microsoft" or "microsoft-en" => MicrosoftRulesUrl,
            "microsoft-it" or "microsoft-italian" => MicrosoftItalianRulesUrl,
            "microsoft-ja" or "microsoft-japanese" => MicrosoftJapaneseRulesUrl,
            "microsoft-es" or "microsoft-spanish" => MicrosoftSpanishRulesUrl,
            _ => throw new ArgumentException(
                $"Unknown BPA ruleset '{key}'. Known rulesets: {string.Join(", ", KnownRulesets)}. Use --rules for a custom file or URL.",
                nameof(ruleset))
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
}
