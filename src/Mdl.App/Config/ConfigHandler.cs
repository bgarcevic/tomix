using Mdl.Core.Configuration;
using Mdl.Core.Results;

namespace Mdl.App.Config;

/// <summary>
/// Reads and writes local MDL preferences. Unknown keys and invalid values are rejected
/// with exit code 2 (invalid arguments) so scripts can react predictably.
/// </summary>
public sealed class ConfigHandler
{
    private readonly MdlConfigStore _store;

    public ConfigHandler()
        : this(new MdlConfigStore())
    {
    }

    public ConfigHandler(MdlConfigStore store) => _store = store;

    public MdlResult<ConfigListResult> List()
    {
        var sorted = _store.Load()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return MdlResult<ConfigListResult>.Ok(new ConfigListResult(sorted));
    }

    public MdlResult<ConfigGetResult> Get(string key)
    {
        if (!ConfigKeys.IsKnown(key))
            return UnknownKey<ConfigGetResult>(key);

        _store.Load().TryGetValue(key, out var value);
        return MdlResult<ConfigGetResult>.Ok(new ConfigGetResult(key, value));
    }

    public MdlResult<ConfigSetResult> Set(string key, string value)
    {
        if (!ConfigKeys.IsKnown(key))
            return UnknownKey<ConfigSetResult>(key);

        if (!TryValidateValue(key, value, out var error))
            return MdlResult<ConfigSetResult>.Fail("MDL_CONFIG_INVALID_VALUE", error, exitCode: 2);

        var values = _store.Load();
        values[key] = value;
        _store.Save(values);

        return MdlResult<ConfigSetResult>.Ok(new ConfigSetResult(key, value));
    }

    private static MdlResult<T> UnknownKey<T>(string key)
        => MdlResult<T>.Fail(
            code: "MDL_CONFIG_UNKNOWN_KEY",
            message: $"Unknown config key: {key}. Known keys: {string.Join(", ", ConfigKeys.All)}.",
            exitCode: 2);

    private static bool TryValidateValue(string key, string value, out string error)
    {
        error = "";

        switch (key)
        {
            case ConfigKeys.DefaultFormat when value is not ("human" or "json"):
                error = "defaultFormat must be 'human' or 'json'.";
                return false;

            case ConfigKeys.NoColor or ConfigKeys.Telemetry or ConfigKeys.HideWarnings
                when !bool.TryParse(value, out _):
                error = $"{key} must be 'true' or 'false'.";
                return false;

            default:
                return true;
        }
    }
}
