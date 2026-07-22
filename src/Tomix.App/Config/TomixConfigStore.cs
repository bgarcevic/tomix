using System.Text.Json;
using Tomix.Core.Configuration;
using Tomix.Platform.Configuration;

namespace Tomix.App.Config;

/// <summary>
/// Reads and writes local tomix preferences as a flat JSON map at <see cref="TomixPaths.ConfigFile"/>.
/// Lives in the application layer because it performs file I/O; the file path is injectable so the
/// store can be exercised in tests without touching the user profile.
/// </summary>
public sealed class TomixConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    public TomixConfigStore(string path) => _path = path;

    public string FilePath => _path;

    public IDictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return NewMap();

        var json = File.ReadAllText(_path);

        if (string.IsNullOrWhiteSpace(json))
            return NewMap();

        Dictionary<string, string>? data;
        try
        {
            data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException ex)
        {
            // Config is user-authored; silently resetting it would lose settings, so
            // surface the corruption instead of self-healing (same policy as profiles).
            throw new InvalidOperationException(
                $"Config file is corrupt: {_path}. Fix or delete it, then re-create settings with 'tx config set'.", ex);
        }

        var values = data is null
            ? NewMap()
            : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);

        if (values.TryGetValue(ConfigKeys.DefaultFormat, out var format) &&
            string.Equals(format, "human", StringComparison.OrdinalIgnoreCase))
            values[ConfigKeys.DefaultFormat] = "text";

        return values;
    }

    public void Save(IDictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(values, SerializerOptions));
    }

    private static Dictionary<string, string> NewMap()
        => new(StringComparer.OrdinalIgnoreCase);
}
