using System.Text.Json;
using Tomix.Core.Configuration;

namespace Tomix.App.State;

public sealed class CliStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configDirectory;

    public CliStateStore()
        : this(TomixPaths.ConfigDirectory)
    {
    }

    public CliStateStore(string configDirectory) => _configDirectory = configDirectory;

    public string ProfilesFile => Path.Combine(_configDirectory, "profiles.json");

    public string SessionsDirectory => Path.Combine(_configDirectory, "sessions");

    public string CurrentSessionId
    {
        get
        {
            var named = Environment.GetEnvironmentVariable("TOMIX_SESSION");
            if (string.IsNullOrWhiteSpace(named))
                named = Environment.GetEnvironmentVariable("TE_SESSION");

            return string.IsNullOrWhiteSpace(named)
                ? "default"
                : named.Trim();
        }
    }

    public string CurrentSessionKind => CurrentSessionId.StartsWith("pid-", StringComparison.OrdinalIgnoreCase)
        ? "pid"
        : "named";

    public string CurrentSessionFile => Path.Combine(SessionsDirectory, $"{SafeFileName(CurrentSessionId)}.json");

    public IDictionary<string, CliProfile> LoadProfiles()
    {
        if (!File.Exists(ProfilesFile))
            return NewProfileMap();

        var json = File.ReadAllText(ProfilesFile);
        if (string.IsNullOrWhiteSpace(json))
            return NewProfileMap();

        var profiles = JsonSerializer.Deserialize<Dictionary<string, CliProfile>>(json);
        return profiles is null
            ? NewProfileMap()
            : new Dictionary<string, CliProfile>(profiles, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveProfiles(IDictionary<string, CliProfile> profiles)
    {
        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, SerializerOptions));
    }

    public CliConnectionState? LoadCurrentSession()
    {
        if (!File.Exists(CurrentSessionFile))
            return null;

        var json = File.ReadAllText(CurrentSessionFile);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CliConnectionState>(json);
    }

    public void SaveCurrentSession(CliConnectionState state)
    {
        Directory.CreateDirectory(SessionsDirectory);
        File.WriteAllText(CurrentSessionFile, JsonSerializer.Serialize(state, SerializerOptions));
    }

    public void ClearCurrentSession()
    {
        if (File.Exists(CurrentSessionFile))
            File.Delete(CurrentSessionFile);
    }

    public IReadOnlyList<SessionFileInfo> ListSessions()
    {
        if (!Directory.Exists(SessionsDirectory))
            return [];

        return Directory.EnumerateFiles(SessionsDirectory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new SessionFileInfo(
                Path.GetFileNameWithoutExtension(path),
                path,
                path.Equals(CurrentSessionFile, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public int PruneSessions(bool all)
    {
        if (!Directory.Exists(SessionsDirectory))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(SessionsDirectory, "*.json"))
        {
            if (file.Equals(CurrentSessionFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = Path.GetFileNameWithoutExtension(file);
            if (!all && IsLivePidSession(id))
                continue;

            File.Delete(file);
            removed++;
        }

        return removed;
    }

    private static bool IsLivePidSession(string sessionId)
    {
        if (!sessionId.StartsWith("pid-", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(sessionId["pid-".Length..], out var pid))
            return false;

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<string, CliProfile> NewProfileMap()
        => new(StringComparer.OrdinalIgnoreCase);

    private static string SafeFileName(string value)
    {
        var safe = value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe;
    }
}

public sealed record SessionFileInfo(
    string SessionId,
    string Path,
    bool Current);
