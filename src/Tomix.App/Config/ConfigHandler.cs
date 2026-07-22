using Tomix.Core.Configuration;
using Tomix.Core.Results;

namespace Tomix.App.Config;

/// <summary>
/// Reads and writes local tomix preferences. Unknown keys and invalid values are rejected
/// with exit code 2 (invalid arguments) so scripts can react predictably.
/// </summary>
public sealed class ConfigHandler
{
    private readonly TomixConfigStore _store;

    public ConfigHandler(TomixConfigStore store) => _store = store;

    public TomixResult<ConfigInitResult> Init(bool force)
    {
        var created = force || !File.Exists(_store.FilePath);

        if (created)
            _store.Save(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        return TomixResult<ConfigInitResult>.Ok(new ConfigInitResult(_store.FilePath, created));
    }

    public TomixResult<ConfigListResult> List()
    {
        var sorted = _store.Load()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var unsupported = sorted.Keys
            .Where(key => !ConfigKeys.IsKnown(key))
            .ToList();

        return TomixResult<ConfigListResult>.Ok(new ConfigListResult(sorted, unsupported));
    }

    public TomixResult<ConfigGetResult> Get(string key)
    {
        if (!ConfigKeys.IsKnown(key))
            return UnknownKey<ConfigGetResult>(key);

        _store.Load().TryGetValue(key, out var value);
        return TomixResult<ConfigGetResult>.Ok(new ConfigGetResult(key, value));
    }

    public TomixResult<ConfigSetResult> Set(string key, string value)
    {
        if (!ConfigKeys.IsKnown(key))
            return UnknownKey<ConfigSetResult>(key);

        var normalized = NormalizeValue(key, value);
        if (!TryValidateValue(key, normalized, out var error))
            return TomixResult<ConfigSetResult>.Fail("TOMIX_CONFIG_INVALID_VALUE", error, exitCode: 2);

        var values = _store.Load();
        values[key] = normalized;
        _store.Save(values);

        return TomixResult<ConfigSetResult>.Ok(new ConfigSetResult(key, normalized));
    }

    private static TomixResult<T> UnknownKey<T>(string key)
        => TomixResult<T>.Fail(
            code: "TOMIX_CONFIG_UNKNOWN_KEY",
            message: $"Unknown config key: {key}. Known keys: {string.Join(", ", ConfigKeys.All)}.",
            exitCode: 2);

    private static bool TryValidateValue(string key, string value, out string error)
    {
        error = "";

        switch (key)
        {
            case ConfigKeys.DefaultFormat when value is not ("text" or "json"):
                error = "defaultFormat must be 'text' or 'json'.";
                return false;

            case ConfigKeys.NoColor or ConfigKeys.UpdateCheck
                when !bool.TryParse(value, out _):
                error = $"{key} must be 'true' or 'false'.";
                return false;

            default:
                return true;
        }
    }

    private static string NormalizeValue(string key, string value)
        => key.Equals(ConfigKeys.DefaultFormat, StringComparison.OrdinalIgnoreCase) &&
           value.Equals("human", StringComparison.OrdinalIgnoreCase)
            ? "text"
            : value.ToLowerInvariant() is "text" or "json" &&
              key.Equals(ConfigKeys.DefaultFormat, StringComparison.OrdinalIgnoreCase)
                ? value.ToLowerInvariant()
                : value;
}
