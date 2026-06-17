using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.Core.Bpa;

/// <summary>
/// Reads and writes the Best-Practice-Analyzer ignore list, an annotation whose value is JSON of the
/// shape <c>{ "RuleIDs": ["RULE_A", ...] }</c>. The same annotation key carries two distinct ignore
/// modes depending on where it lives: on the model object it disables a rule globally; on an
/// individual object it suppresses that object's violation of the rule.
///
/// Rule IDs are compared case-insensitively. Reads prefer the correctly-spelled key and fall back to
/// the historical misspelled key; writes always emit the correct key and drop the misspelled one.
/// Malformed JSON is treated as an empty list rather than throwing.
/// </summary>
public static class BpaIgnoreStore
{
    /// <summary>The correctly-spelled annotation key.</summary>
    public const string Key = "BestPracticeAnalyzer_IgnoreRules";

    /// <summary>The historical misspelled key, still read for backward compatibility.</summary>
    public const string LegacyKey = "BestPractizeAnalyzer_IgnoreRules";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    /// <summary>Parses the ignored rule IDs from a raw annotation value (case-insensitive).</summary>
    public static IReadOnlySet<string> ParseRuleIds(string? annotationValue)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(annotationValue))
            return set;

        try
        {
            var parsed = JsonSerializer.Deserialize<IgnorePayload>(annotationValue);
            if (parsed?.RuleIDs is { } ids)
                foreach (var id in ids)
                    if (!string.IsNullOrWhiteSpace(id))
                        set.Add(id);
        }
        catch (JsonException)
        {
            // Malformed annotation JSON must not crash analysis — treat as no ignores.
        }

        return set;
    }

    /// <summary>
    /// Reads the ignored rule IDs stored on a model object, preferring the correct key and falling
    /// back to the historical misspelled key.
    /// </summary>
    public static IReadOnlySet<string> ReadRuleIds(ModelObject obj)
    {
        var primary = obj.Property($"Annotation:{Key}");
        if (!string.IsNullOrWhiteSpace(primary))
            return ParseRuleIds(primary);

        return ParseRuleIds(obj.Property($"Annotation:{LegacyKey}"));
    }

    /// <summary>Whether <paramref name="ruleId"/> is present in the object's ignore list.</summary>
    public static bool IsIgnored(ModelObject obj, string ruleId)
        => ReadRuleIds(obj).Contains(ruleId);

    /// <summary>
    /// Reads the ignored rule IDs from a property bag (e.g. <see cref="ModelSnapshot.Properties"/>),
    /// preferring the correct key and falling back to the historical misspelled key.
    /// </summary>
    public static IReadOnlySet<string> ReadRuleIds(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (properties.TryGetValue($"Annotation:{Key}", out var primary) && !string.IsNullOrWhiteSpace(primary))
            return ParseRuleIds(primary);

        return ParseRuleIds(properties.TryGetValue($"Annotation:{LegacyKey}", out var legacy) ? legacy : null);
    }

    /// <summary>Whether the historical misspelled key is present in a property bag.</summary>
    public static bool HasLegacyKey(IReadOnlyDictionary<string, string>? properties)
        => properties is not null && properties.ContainsKey($"Annotation:{LegacyKey}");

    /// <summary>Serializes a set of rule IDs to the persisted <c>{ "RuleIDs": [...] }</c> JSON shape.</summary>
    public static string Serialize(IEnumerable<string> ruleIds)
        => JsonSerializer.Serialize(
            new IgnorePayload
            {
                RuleIDs = ruleIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            },
            WriteOptions);

    private sealed class IgnorePayload
    {
        [JsonPropertyName("RuleIDs")]
        public List<string>? RuleIDs { get; set; }
    }
}
