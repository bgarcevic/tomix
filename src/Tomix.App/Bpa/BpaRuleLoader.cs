using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

public sealed class BpaRuleLoader
{
    public const string StandardRuleset = "standard";

    private const string BundledRulesFileName = "bpa-rules.json";
    private const string MicrosoftRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/BPARules.json";
    private const string MicrosoftItalianRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Italian/BPARules.json";
    private const string MicrosoftJapaneseRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Japanese/BPARules.json";
    private const string MicrosoftSpanishRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/Spanish/BPARules.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<string> KnownRulesets { get; } =
    [
        StandardRuleset,
        "microsoft",
        "microsoft-it",
        "microsoft-ja",
        "microsoft-es"
    ];

    public static IReadOnlyList<BpaRule> LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        return ParseRules(json);
    }

    public static IReadOnlyList<BpaRule> LoadFromJson(string json)
        => ParseRules(json);

    public static IReadOnlyList<BpaRule> LoadDefaultRules()
        => LoadBundledRules();

    public static Task<IReadOnlyList<BpaRule>> LoadDefaultRulesAsync(CancellationToken cancellationToken)
        => LoadRulesetAsync(StandardRuleset, cancellationToken);

    public static async Task<IReadOnlyList<BpaRule>> LoadRulesetAsync(
        string? ruleset,
        CancellationToken cancellationToken)
    {
        var source = ResolveRulesetSource(ruleset);
        if (source == StandardRuleset)
            return LoadBundledRules();

        return await LoadFromSourceAsync(source, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<BpaRule>> LoadFromSourceAsync(
        string source,
        CancellationToken cancellationToken)
    {
        if (TryCreateHttpUri(source, out var uri))
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("tomix-cli");
            var json = await http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            return ParseRules(json);
        }

        if (!File.Exists(source))
            throw new FileNotFoundException($"BPA rules file not found: {source}", source);

        return LoadFromFile(source);
    }

    private static IReadOnlyList<BpaRule> ParseRules(string json)
    {
        var raw = JsonSerializer.Deserialize<List<JsonRule>>(json, Options);
        if (raw is null) return [];

        return raw.Select(r => new BpaRule(
            r.Id ?? "",
            r.Name ?? "",
            r.Category ?? "",
            MapSeverity(r.Severity),
            ParseScope(r.Scope ?? ""),
            r.Description,
            r.Expression,
            r.FixExpression,
            r.CompatibilityLevel)).ToList();
    }

    private static BpaSeverity MapSeverity(int severity) => severity switch
    {
        1 => BpaSeverity.Info,
        2 => BpaSeverity.Warning,
        3 => BpaSeverity.Error,
        _ => BpaSeverity.Info
    };

    private static IReadOnlyList<string> ParseScope(string scope)
    {
        return scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .ToList();
    }

    private static string ResolveRulesetSource(string? ruleset)
    {
        var key = string.IsNullOrWhiteSpace(ruleset)
            ? StandardRuleset
            : ruleset.Trim();

        if (key.Equals("default", StringComparison.OrdinalIgnoreCase)
            || key.Equals("bundled", StringComparison.OrdinalIgnoreCase)
            || key.Equals(StandardRuleset, StringComparison.OrdinalIgnoreCase))
            return StandardRuleset;

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

    private static IReadOnlyList<BpaRule> LoadBundledRules()
    {
        foreach (var path in CandidateBundledRulePaths())
        {
            if (File.Exists(path))
                return LoadFromFile(path);
        }

        return BuiltInRules();
    }

    private static IEnumerable<string> CandidateBundledRulePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Bpa", "Rules", BundledRulesFileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "Bpa", "Rules", BundledRulesFileName);

        foreach (var root in Ancestors(AppContext.BaseDirectory).Concat(Ancestors(Directory.GetCurrentDirectory())))
            yield return Path.Combine(root, "src", "Tomix.App", "Bpa", "Rules", BundledRulesFileName);

        yield return Path.Combine(AppContext.BaseDirectory, BundledRulesFileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), BundledRulesFileName);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var directory = new DirectoryInfo(path);
        if (File.Exists(path))
            directory = directory.Parent!;

        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
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

    private static IReadOnlyList<BpaRule> BuiltInRules()
    {
        return
        [
            new("AVOID_FLOATING_POINT_DATA_TYPES",
                "[Performance] Do not use floating point data types",
                "Performance", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "The Double floating point data type should be avoided. Use Int64 or Decimal where appropriate."),
            new("ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS",
                "[Performance] Set IsAvailableInMdx to false on non-attribute columns",
                "Performance", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Attribute hierarchies should not be built for columns never used for slicing by MDX clients."),
            new("SNOWFLAKE_SCHEMA_ARCHITECTURE",
                "[Performance] Consider a star-schema instead of a snowflake architecture",
                "Performance", BpaSeverity.Warning,
                ["Table", "CalculatedTable"],
                "A star-schema is generally the optimal architecture for tabular models."),
            new("MODEL_SHOULD_HAVE_A_DATE_TABLE",
                "[Performance] Model should have a date table",
                "Performance", BpaSeverity.Warning,
                ["Model"],
                "Models should generally have a date table."),
            new("REMOVE_AUTO-DATE_TABLE",
                "[Performance] Remove auto-date table",
                "Performance", BpaSeverity.Warning,
                ["Table", "CalculatedTable"],
                "Avoid using auto-date tables."),
            new("AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS",
                "[Performance] Avoid excessive bi-directional or many-to-many relationships",
                "Performance", BpaSeverity.Warning,
                ["Model"],
                "Limit use of bi-di and many-to-many relationships."),
            new("REDUCE_USAGE_OF_CALCULATED_TABLES",
                "[Performance] Reduce usage of calculated tables",
                "Performance", BpaSeverity.Warning,
                ["CalculatedTable"],
                "Migrate calculated table logic to your data warehouse."),
            new("REDUCE_NUMBER_OF_CALCULATED_COLUMNS",
                "[Performance] Reduce number of calculated columns",
                "Performance", BpaSeverity.Warning,
                ["Model"],
                "Calculated columns do not compress as well as data columns."),
            new("MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION",
                "[Performance] Many-to-many relationships should be single-direction",
                "Performance", BpaSeverity.Warning,
                ["Relationship"],
                "Many-to-many relationships should use single-direction cross-filtering."),
            new("CHECK_IF_BI-DIRECTIONAL_AND_MANY-TO-MANY_RELATIONSHIPS_ARE_VALID",
                "[Performance] Check if bi-directional and many-to-many relationships are valid",
                "Performance", BpaSeverity.Info,
                ["Relationship"],
                "Bi-directional and many-to-many relationships may cause performance degradation."),
            new("RELATIONSHIP_COLUMNS_SAME_DATA_TYPE",
                "[Error Prevention] Relationship columns should be of the same data type",
                "Error Prevention", BpaSeverity.Error,
                ["Relationship"],
                "Columns used in a relationship should be of the same data type."),
            new("DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN",
                "[Error Prevention] Data columns must have a source column",
                "Error Prevention", BpaSeverity.Error,
                ["DataColumn"],
                "Data columns without a source column will cause an error when processing."),
            new("EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION",
                "[Error Prevention] Expression-reliant objects must have an expression",
                "Error Prevention", BpaSeverity.Error,
                ["Measure", "CalculatedColumn", "CalculationItem"],
                "Calculated columns, calculation items and measures must have an expression."),
            new("SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS",
                "[Error Prevention] Set IsAvailableInMdx to true on necessary columns",
                "Error Prevention", BpaSeverity.Error,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Attribute hierarchies should be enabled if a column is used in sorting, hierarchies, or variations."),
            new("DAX_COLUMNS_FULLY_QUALIFIED",
                "[DAX Expressions] Column references should be fully qualified",
                "DAX Expressions", BpaSeverity.Error,
                ["Measure", "KPI", "TablePermission", "CalculationItem"],
                "Using fully qualified column references makes it easier to distinguish between column and measure references."),
            new("DAX_MEASURES_UNQUALIFIED",
                "[DAX Expressions] Measure references should be unqualified",
                "DAX Expressions", BpaSeverity.Error,
                ["Measure", "CalculatedColumn", "CalculatedTable", "KPI", "CalculationItem"],
                "Using unqualified measure references avoids certain errors."),
            new("AVOID_DUPLICATE_MEASURES",
                "[DAX Expressions] No two measures should have the same definition",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure"],
                "Two measures with different names and the same DAX expression should be avoided."),
            new("USE_THE_DIVIDE_FUNCTION_FOR_DIVISION",
                "[DAX Expressions] Use the DIVIDE function for division",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure", "CalculatedColumn", "CalculationItem"],
                "Use the DIVIDE function instead of / to handle divide-by-zero cases."),
            new("AVOID_USING_THE_IFERROR_FUNCTION",
                "[DAX Expressions] Avoid using the IFERROR function",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure", "CalculatedColumn"],
                "Avoid using the IFERROR function. Use DIVIDE instead."),
            new("MEASURES_SHOULD_NOT_BE_DIRECT_REFERENCES_OF_OTHER_MEASURES",
                "[DAX Expressions] Measures should not be direct references of other measures",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure"],
                "Measures that are simply a reference to another measure should be removed."),
            new("FILTER_COLUMN_VALUES",
                "[DAX Expressions] Filter column values with proper syntax",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure", "CalculatedColumn", "CalculationItem"],
                "Use KEEPFILTERS or direct column filters instead of FILTER(Table, Table[Column]=\"Value\")."),
            new("FILTER_MEASURE_VALUES_BY_COLUMNS",
                "[DAX Expressions] Filter measure values by columns, not tables",
                "DAX Expressions", BpaSeverity.Warning,
                ["Measure", "CalculatedColumn", "CalculationItem"],
                "Use FILTER(VALUES(Table[Column]),[Measure] > Value) instead of FILTER(Table,[Measure]>Value)."),
            new("INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED",
                "[DAX Expressions] Inactive relationships that are never activated",
                "DAX Expressions", BpaSeverity.Warning,
                ["Relationship"],
                "Inactive relationships not referenced via USERELATIONSHIP will not be used."),
            new("EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS",
                "[DAX Expressions] The EVALUATEANDLOG function should not be used in production models",
                "DAX Expressions", BpaSeverity.Info,
                ["Measure"],
                "EVALUATEANDLOG is meant for development/test only."),
            new("UNNECESSARY_COLUMNS",
                "[Maintenance] Remove unnecessary columns",
                "Maintenance", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Hidden columns not referenced by DAX, relationships, hierarchies, or Sort By should be removed."),
            new("UNNECESSARY_MEASURES",
                "[Maintenance] Remove unnecessary measures",
                "Maintenance", BpaSeverity.Warning,
                ["Measure"],
                "Hidden measures not referenced by any DAX expressions should be removed."),
            new("ENSURE_TABLES_HAVE_RELATIONSHIPS",
                "[Maintenance] Ensure tables have relationships",
                "Maintenance", BpaSeverity.Info,
                ["Table", "CalculatedTable"],
                "Tables not connected to any other table with a relationship should be reviewed."),
            new("OBJECTS_WITH_NO_DESCRIPTION",
                "[Maintenance] Visible objects with no description",
                "Maintenance", BpaSeverity.Info,
                ["Table", "Measure", "DataColumn", "CalculatedColumn", "CalculatedTable", "CalculationGroup"],
                "Add descriptions to objects."),
            new("CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS",
                "[Maintenance] Calculation groups with no calculation items",
                "Maintenance", BpaSeverity.Warning,
                ["CalculationGroup"],
                "Calculation groups without calculation items have no function."),
            new("PARTITION_NAME_SHOULD_MATCH_TABLE_NAME_FOR_SINGLE_PARTITION_TABLES",
                "[Naming Conventions] Partition name should match table name for single partition tables",
                "Naming Conventions", BpaSeverity.Info,
                ["Table"],
                "Single-partition tables should have partition names matching the table name."),
            new("SPECIAL_CHARS_IN_OBJECT_NAMES",
                "[Naming Conventions] Object names must not contain special characters",
                "Naming Conventions", BpaSeverity.Warning,
                ["Model", "Table", "Measure", "Hierarchy", "Perspective", "Partition", "DataColumn",
                    "CalculatedColumn", "CalculatedTable", "CalculationGroup", "CalculationItem"],
                "Object names should not include tabs, line breaks, etc."),
            new("TRIM_OBJECT_NAMES",
                "[Naming Conventions] Trim object names",
                "Naming Conventions", BpaSeverity.Info,
                ["Model", "Table", "Measure", "Hierarchy", "Level", "Perspective", "Partition",
                    "DataColumn", "CalculatedColumn", "CalculatedTable", "CalculationGroup", "CalculationItem"],
                "Object names should not start or end with a space."),
            new("FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS",
                "[Formatting] Format flag columns as Yes/No value strings",
                "Formatting", BpaSeverity.Info,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Flags should be formatted as Yes/No strings."),
            new("PROVIDE_FORMAT_STRING_FOR_MEASURES",
                "[Formatting] Provide format string for measures",
                "Formatting", BpaSeverity.Error,
                ["Measure"],
                "Visible measures should have their format string property assigned."),
            new("NUMERIC_COLUMN_SUMMARIZE_BY",
                "[Formatting] Do not summarize numeric columns",
                "Formatting", BpaSeverity.Error,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Numeric columns should have SummarizeBy set to None."),
            new("HIDE_FOREIGN_KEYS",
                "[Formatting] Hide foreign keys",
                "Formatting", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Foreign keys should always be hidden."),
            new("MARK_PRIMARY_KEYS",
                "[Formatting] Mark primary keys",
                "Formatting", BpaSeverity.Info,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Set the Key property to true for primary key columns."),
            new("DATECOLUMN_FORMATSTRING",
                "[Formatting] Provide format string for Date columns",
                "Formatting", BpaSeverity.Info,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Columns of type DateTime with Date in their names should be formatted."),
            new("MONTHCOLUMN_FORMATSTRING",
                "[Formatting] Provide format string for Month columns",
                "Formatting", BpaSeverity.Info,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Columns of type DateTime with Month in their names should be formatted as MMMM yyyy."),
            new("PERCENTAGE_FORMATTING",
                "[Formatting] Percentages should be formatted with thousands separators and 1 decimal",
                "Formatting", BpaSeverity.Warning,
                ["Measure"],
                "Percentage measures should be properly formatted."),
            new("INTEGER_FORMATTING",
                "[Formatting] Whole numbers should be formatted with thousands separators",
                "Formatting", BpaSeverity.Warning,
                ["Measure"],
                "Whole number measures should be formatted with thousands separators."),
            new("ADD_DATA_CATEGORY_FOR_COLUMNS",
                "[Formatting] Add data category for columns",
                "Formatting", BpaSeverity.Info,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Add Data Category property for appropriate columns."),
            new("FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED",
                "[Formatting] First letter of objects must be capitalized",
                "Formatting", BpaSeverity.Info,
                ["Table", "Measure", "Hierarchy", "CalculatedColumn", "CalculatedTable", "CalculationGroup"],
                "The first letter of object names should be capitalized."),
            new("OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE",
                "[Formatting] Objects should not start or end with a space",
                "Formatting", BpaSeverity.Error,
                ["Model", "Table", "Measure", "Hierarchy", "Perspective", "Partition", "DataColumn",
                    "CalculatedColumn"],
                "Objects should not start or end with a space."),
            new("MONTH_(AS_A_STRING)_MUST_BE_SORTED",
                "[Formatting] Month (as a string) must be sorted",
                "Formatting", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                "Month string columns that are not sorted will sort alphabetically.")
        ];
    }
}
