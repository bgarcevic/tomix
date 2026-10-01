using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.Core.Bpa;
using Tomix.Core.Results;
using Tomix.Core.Rules;
using Tomix.Platform.Configuration;

namespace Tomix.App.Bpa;

/// <summary>
/// The rule fields <c>bpa rules add</c> and <c>set</c> accept. A null field is left alone; an
/// empty optional field (description, fix expression) removes it from the rule.
/// </summary>
public sealed record BpaRuleFields(
    string? Name = null,
    string? Category = null,
    string? Severity = null,
    string? Scope = null,
    string? Expression = null,
    string? Description = null,
    string? FixExpression = null)
{
    public bool IsEmpty => Name is null && Category is null && Severity is null && Scope is null
        && Expression is null && Description is null && FixExpression is null;
}

/// <summary>
/// The result shared by <c>bpa rules add/set/remove/init</c>. <see cref="Rule"/> is the rule as
/// written (null for <c>remove</c> and <c>init</c>); <see cref="ChangedFields"/> names the JSON
/// fields <c>set</c> changed.
/// </summary>
public sealed record BpaRulesFileResult(
    string Action,
    string Path,
    bool Changed,
    int RuleCount,
    string? RuleId = null,
    BpaRuleInfo? Rule = null,
    IReadOnlyList<string>? ChangedFields = null);

/// <summary>
/// A BPA rule array edited in place by the rule-authoring commands: a rules JSON file (the
/// selected <c>--rules-file</c>, or the user's config-dir <c>bpa-rules.json</c>, which
/// <c>bpa run</c> loads on every run), or the model's <c>BestPracticeAnalyzer</c> annotation. The
/// rules are handled as a raw JSON array so fields tx does not model (and the order of existing
/// ones) survive an edit.
/// </summary>
internal sealed class BpaRulesFile
{
    public const string UserFileName = "bpa-rules.json";

    /// <summary>The source <c>bpa rules list</c> reports for rules in the model annotation.</summary>
    public const string ModelSource = "model-embedded";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The field order the bundled catalog uses; a new rule is written in it.</summary>
    private static readonly string[] FieldOrder =
        ["ID", "Name", "Category", "Description", "Severity", "Scope", "Expression", "FixExpression", "CompatibilityLevel"];

    /// <summary>
    /// Scope tokens a rule may name: every scope the bundled catalog uses, the engine's broader
    /// <c>Column</c>/<c>DataSource</c> tokens, and scopes community rule files use.
    /// </summary>
    internal static readonly IReadOnlyList<string> KnownScopes =
    [
        "Model", "Table", "CalculatedTable", "CalculationGroup", "CalculationItem",
        "Column", "DataColumn", "CalculatedColumn", "CalculatedTableColumn",
        "Measure", "KPI", "Hierarchy", "Level", "Relationship", "Partition", "Perspective",
        "Culture", "DataSource", "ProviderDataSource", "StructuredDataSource",
        "NamedExpression", "ModelRole", "ModelRoleMember", "TablePermission", "Variation"
    ];

    private readonly JsonArray _rules;

    private BpaRulesFile(string path, string source, JsonArray rules)
    {
        Path = path;
        Source = source;
        _rules = rules;
    }

    /// <summary>The file path, or a description of the annotation for a model's rules.</summary>
    public string Path { get; }

    /// <summary>The rule source <see cref="Describe"/> reports: the path, or <see cref="ModelSource"/>.</summary>
    public string Source { get; }

    public int Count => _rules.Count;

    /// <summary>
    /// The target path: <paramref name="rulesFile"/> when given, else the user file. Returns a
    /// failure (and no path) for a remote rules file, which cannot be edited in place.
    /// </summary>
    public static TomixResult<BpaRulesFileResult>? TryResolvePath(
        string configDirectory, string? rulesFile, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(rulesFile))
        {
            path = System.IO.Path.Combine(configDirectory, UserFileName);
            return null;
        }

        if (Uri.TryCreate(rulesFile, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULES_FILE_REMOTE",
                $"Cannot edit a remote rules file: {rulesFile}",
                exitCode: 2,
                hint: "Download it and pass the local path to --rules-file.");

        path = System.IO.Path.GetFullPath(rulesFile);
        return null;
    }

    /// <summary>An empty rules file at <paramref name="path"/>, not yet written.</summary>
    public static BpaRulesFile Empty(string path) => new(path, path, []);

    /// <summary>
    /// The rules in a model's <c>BestPracticeAnalyzer</c> annotation (<paramref name="json"/>, null
    /// or blank when the model has none). Returns false with a reason when the annotation is not a
    /// JSON array of rule objects, so a malformed annotation is never overwritten.
    /// </summary>
    public static bool TryFromAnnotation(string? json, out BpaRulesFile file, out string reason)
    {
        file = null!;
        if (!TryParse(json, out var rules, out reason))
            return false;

        file = new BpaRulesFile($"the model's {BpaModelRuleLoader.EmbeddedKey} annotation", ModelSource, rules);
        return true;
    }

    /// <summary>Parses a rule array; blank text is an empty array.</summary>
    private static bool TryParse(string? text, out JsonArray rules, out string reason)
    {
        rules = [];
        reason = "";
        try
        {
            var node = string.IsNullOrWhiteSpace(text) ? new JsonArray() : JsonNode.Parse(text);
            if (node is not JsonArray array || array.Any(item => item is not JsonObject))
            {
                reason = "expected a JSON array of rule objects.";
                return false;
            }

            rules = array;
            return true;
        }
        catch (JsonException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolves and reads the target file, returning a failure (and no file) when it cannot be
    /// edited. A missing file fails unless <paramref name="createIfMissing"/>; a file that is not a
    /// JSON array of rules fails with <c>TOMIX_BPA_RULES_LOAD_FAILED</c>.
    /// </summary>
    public static TomixResult<BpaRulesFileResult>? TryOpen(
        string configDirectory, string? rulesFile, bool createIfMissing, out BpaRulesFile file)
    {
        file = null!;
        if (TryResolvePath(configDirectory, rulesFile, out var path) is { } remote)
            return remote;

        if (!File.Exists(path))
        {
            if (createIfMissing)
            {
                file = Empty(path);
                return null;
            }

            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULES_FILE_NOT_FOUND",
                $"BPA rules file not found: {path}",
                exitCode: 2,
                hint: "Create it with 'tx bpa rules init', or add a rule with 'tx bpa rules add'.");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LoadFailed($"BPA rules file {path}", ex.Message);
        }

        if (!TryParse(text, out var rules, out var reason))
            return LoadFailed($"BPA rules file {path}", reason);

        file = new BpaRulesFile(path, path, rules);
        return null;
    }

    /// <summary><c>TOMIX_BPA_RULES_LOAD_FAILED</c> for rules that cannot be read, and so are not edited.</summary>
    internal static TomixResult<BpaRulesFileResult> LoadFailed(string what, string reason)
        => TomixResult<BpaRulesFileResult>.Fail(
            "TOMIX_BPA_RULES_LOAD_FAILED",
            $"Cannot read {what}: {reason}",
            exitCode: 2);

    public JsonObject? Find(string ruleId)
        => _rules.OfType<JsonObject>().FirstOrDefault(
            rule => string.Equals(StringField(rule, "ID"), ruleId, StringComparison.OrdinalIgnoreCase));

    public void Remove(JsonObject rule) => _rules.Remove(rule);

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllText(Path, _rules.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    /// <summary>The rules as compact JSON, the form the model annotation holds.</summary>
    public string ToAnnotationJson() => _rules.ToJsonString();

    /// <summary>
    /// Validates a new rule and adds it in the catalog's field order. Name, scope, and expression
    /// are required; category defaults to <c>Custom</c> and severity to warning. An ID the rules
    /// already have fails, so an add never silently overwrites a rule.
    /// </summary>
    /// <param name="setTarget">What follows the rule ID in the suggested <c>set</c> command (the model, for model rules).</param>
    public TomixResult<BpaRulesFileResult>? TryAdd(
        string ruleId, BpaRuleFields fields, out JsonObject rule, string setTarget = "")
    {
        rule = null!;
        var missing = new[]
            {
                ("--name", fields.Name),
                ("--scope", fields.Scope),
                ("--expression", fields.Expression)
            }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2))
            .Select(f => f.Item1)
            .ToList();
        if (missing.Count > 0)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_FIELD_REQUIRED",
                $"A new rule needs {string.Join(", ", missing)}.",
                exitCode: 2);

        if (Find(ruleId) is not null)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULE_EXISTS",
                $"Rule '{ruleId}' already exists in {Path}.",
                exitCode: 2,
                hint: $"Change it with 'tx bpa rules set {ruleId}{setTarget}', or remove it first.");

        var draft = new JsonObject
        {
            ["ID"] = ruleId,
            ["Category"] = BpaRulesAddHandler.DefaultCategory,
            ["Severity"] = 2,
            ["CompatibilityLevel"] = 1200
        };
        if (TryApply(draft, fields, out _) is { } invalid)
            return invalid;

        rule = new JsonObject();
        foreach (var field in FieldOrder)
            if (draft[field] is { } value)
                rule[field] = value.DeepClone();

        _rules.Add(rule);
        return null;
    }

    /// <summary>
    /// Applies <paramref name="fields"/> to <paramref name="rule"/>, validating each one first so
    /// a bad value changes nothing. Returns a failure, or null with the JSON field names whose
    /// value changed in <paramref name="changed"/>.
    /// </summary>
    public static TomixResult<BpaRulesFileResult>? TryApply(
        JsonObject rule, BpaRuleFields fields, out IReadOnlyList<string> changed)
    {
        changed = [];
        int? severity = null;
        if (fields.Severity is not null)
        {
            if (!TryParseSeverity(fields.Severity, out var parsed))
                return TomixResult<BpaRulesFileResult>.Fail(
                    "TOMIX_BPA_RULE_INVALID_SEVERITY",
                    $"Invalid severity '{fields.Severity}'.",
                    exitCode: 2,
                    hint: "Use error, warning, or info (or 3, 2, 1).");
            severity = (int)parsed;
        }

        string? scope = null;
        if (fields.Scope is not null)
        {
            var normalized = NormalizeScope(fields.Scope, out var unknown);
            if (unknown.Count > 0 || normalized.Length == 0)
                return TomixResult<BpaRulesFileResult>.Fail(
                    "TOMIX_BPA_RULE_INVALID_SCOPE",
                    unknown.Count > 0
                        ? $"Unknown rule scope: {string.Join(", ", unknown)}."
                        : "A rule scope is required.",
                    exitCode: 2,
                    hint: $"Known scopes: {string.Join(", ", KnownScopes)}.");
            scope = normalized;
        }

        foreach (var (field, value) in new[] { ("Name", fields.Name), ("Expression", fields.Expression) })
            if (value is not null && string.IsNullOrWhiteSpace(value))
                return TomixResult<BpaRulesFileResult>.Fail(
                    "TOMIX_BPA_RULE_FIELD_REQUIRED",
                    $"The rule's {field.ToLowerInvariant()} cannot be empty.",
                    exitCode: 2);

        var fieldsChanged = new List<string>();
        SetString(rule, "Name", fields.Name, fieldsChanged);
        SetString(rule, "Category", fields.Category, fieldsChanged);
        SetString(rule, "Description", fields.Description, fieldsChanged);
        if (severity is { } s
            && !(rule["Severity"] is JsonValue current && current.TryGetValue<int>(out var existing) && existing == s))
        {
            rule["Severity"] = s;
            fieldsChanged.Add("Severity");
        }

        SetString(rule, "Scope", scope, fieldsChanged);
        SetString(rule, "Expression", fields.Expression, fieldsChanged);
        SetString(rule, "FixExpression", fields.FixExpression, fieldsChanged);
        changed = fieldsChanged;
        return null;
    }

    /// <summary>
    /// Sets a string field. An empty value removes an optional field; setting the value it
    /// already has is not a change.
    /// </summary>
    private static void SetString(JsonObject rule, string field, string? value, List<string> changed)
    {
        if (value is null)
            return;

        if (value.Length == 0)
        {
            if (rule.Remove(field))
                changed.Add(field);
            return;
        }

        if (StringField(rule, field) == value)
            return;

        rule[field] = value;
        changed.Add(field);
    }

    private static string? StringField(JsonObject rule, string field)
        => rule[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool TryParseSeverity(string value, out RuleSeverity severity)
    {
        severity = value.Trim().ToLowerInvariant() switch
        {
            "error" or "3" => RuleSeverity.Error,
            "warning" or "2" => RuleSeverity.Warning,
            "info" or "1" => RuleSeverity.Info,
            _ => 0
        };
        return severity != 0;
    }

    /// <summary>
    /// Canonical casing, duplicates dropped, joined as the catalog writes it ("Measure, Column").
    /// </summary>
    internal static string NormalizeScope(string scope, out List<string> unknown)
    {
        unknown = [];
        var tokens = new List<string>();
        foreach (var raw in scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var known = KnownScopes.FirstOrDefault(s => s.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                unknown.Add(raw);
            else if (!tokens.Contains(known))
                tokens.Add(known);
        }

        return string.Join(", ", tokens);
    }

    /// <summary>The rule as the listing commands describe it, sourced from these rules.</summary>
    public BpaRuleInfo Describe(JsonObject rule)
    {
        var parsed = BpaRuleLoader.LoadFromJson(new JsonArray(rule.DeepClone()).ToJsonString())[0];
        return new BpaRuleInfo(
            Source,
            Status: "active",
            parsed.Id,
            parsed.Name,
            parsed.Category,
            parsed.Severity,
            string.Join(", ", parsed.Scope),
            parsed.Description,
            parsed.Expression,
            parsed.FixExpression,
            Enabled: true);
    }
}
