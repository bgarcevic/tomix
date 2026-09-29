using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.Core.Bpa;
using Tomix.Core.Results;
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
/// A BPA rules JSON file edited in place by the rule-authoring commands. The file is handled as a
/// raw JSON array so fields tx does not model (and the order of existing ones) survive an edit.
/// The target is the selected <c>--rules-file</c>, or the user's config-dir <c>bpa-rules.json</c>,
/// which <c>bpa run</c> loads on every run.
/// </summary>
internal sealed class BpaRulesFile
{
    public const string UserFileName = "bpa-rules.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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

    private BpaRulesFile(string path, JsonArray rules)
    {
        Path = path;
        _rules = rules;
    }

    public string Path { get; }

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
    public static BpaRulesFile Empty(string path) => new(path, []);

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

        try
        {
            var text = File.ReadAllText(path);
            var node = string.IsNullOrWhiteSpace(text) ? new JsonArray() : JsonNode.Parse(text);
            if (node is not JsonArray array || array.Any(item => item is not JsonObject))
                return LoadFailed(path, "expected a JSON array of rule objects.");

            file = new BpaRulesFile(path, array);
            return null;
        }
        catch (JsonException ex)
        {
            return LoadFailed(path, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LoadFailed(path, ex.Message);
        }
    }

    private static TomixResult<BpaRulesFileResult> LoadFailed(string path, string reason)
        => TomixResult<BpaRulesFileResult>.Fail(
            "TOMIX_BPA_RULES_LOAD_FAILED",
            $"Cannot read BPA rules file {path}: {reason}",
            exitCode: 2);

    public JsonObject? Find(string ruleId)
        => _rules.OfType<JsonObject>().FirstOrDefault(
            rule => string.Equals(StringField(rule, "ID"), ruleId, StringComparison.OrdinalIgnoreCase));

    public void Add(JsonObject rule) => _rules.Add(rule);

    public void Remove(JsonObject rule) => _rules.Remove(rule);

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllText(Path, _rules.ToJsonString(WriteOptions) + Environment.NewLine);
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

    internal static bool TryParseSeverity(string value, out BpaSeverity severity)
    {
        severity = value.Trim().ToLowerInvariant() switch
        {
            "error" or "3" => BpaSeverity.Error,
            "warning" or "2" => BpaSeverity.Warning,
            "info" or "1" => BpaSeverity.Info,
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

    /// <summary>The rule as the listing commands describe it, sourced from this file.</summary>
    public BpaRuleInfo Describe(JsonObject rule)
    {
        var parsed = BpaRuleLoader.LoadFromJson(new JsonArray(rule.DeepClone()).ToJsonString())[0];
        return new BpaRuleInfo(
            Path,
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
