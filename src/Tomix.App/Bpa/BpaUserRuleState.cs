using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Platform.Configuration;

namespace Tomix.App.Bpa;

/// <summary>
/// User-level enable/disable state for BPA rules, persisted at
/// <c>{config}/bpa-disabled.json</c> (<c>~/.tomix</c> or <c>$TOMIX_CONFIG_DIR</c>). A disabled rule is
/// skipped for that user across all models, independent of any model-level ignore annotation.
/// Rule IDs are compared case-insensitively.
/// </summary>
public sealed class BpaUserRuleState
{
    private const string FileName = "bpa-disabled.json";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;

    public BpaUserRuleState(string configDirectory)
        => _path = Path.Combine(configDirectory, FileName);

    /// <summary>The set of user-disabled rule IDs (empty when the file is absent or unreadable).</summary>
    public IReadOnlySet<string> GetDisabled()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path))
            return set;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_path));
            if (payload?.DisabledRuleIDs is { } ids)
                foreach (var id in ids)
                    if (!string.IsNullOrWhiteSpace(id))
                        set.Add(id);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt/locked state file is treated as "nothing disabled" rather than failing analysis.
        }

        return set;
    }

    /// <summary>Disables a rule; returns whether the set changed.</summary>
    public bool Disable(string ruleId) => Mutate(ruleId, disable: true);

    /// <summary>Re-enables a rule; returns whether the set changed.</summary>
    public bool Enable(string ruleId) => Mutate(ruleId, disable: false);

    private bool Mutate(string ruleId, bool disable)
    {
        var set = new HashSet<string>(GetDisabled(), StringComparer.OrdinalIgnoreCase);
        var changed = disable ? set.Add(ruleId) : set.Remove(ruleId);
        if (changed)
            Save(set);
        return changed;
    }

    private void Save(IEnumerable<string> ids)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var payload = new Payload
        {
            DisabledRuleIDs = ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(payload, WriteOptions));
    }

    private sealed class Payload
    {
        [JsonPropertyName("DisabledRuleIDs")]
        public List<string>? DisabledRuleIDs { get; set; }
    }
}
