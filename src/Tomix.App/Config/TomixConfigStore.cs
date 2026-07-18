using System.Text.Json;
using Tomix.Core.Configuration;

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

        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        return data is null
            ? NewMap()
            : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
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
