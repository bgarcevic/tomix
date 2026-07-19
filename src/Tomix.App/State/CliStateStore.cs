using System.Text.Json;
using Tomix.Platform.Configuration;

namespace Tomix.App.State;

public sealed class CliStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configDirectory;

    public CliStateStore(string configDirectory) => _configDirectory = configDirectory;

    public const int MaxRecentConnections = 20;

    public string ProfilesFile => Path.Combine(_configDirectory, "profiles.json");

    public string RecentConnectionsFile => Path.Combine(_configDirectory, "recent-connections.json");

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

        Dictionary<string, CliProfile>? profiles;
        try
        {
            profiles = JsonSerializer.Deserialize<Dictionary<string, CliProfile>>(json);
        }
        catch (JsonException ex)
        {
            // Profiles are user-authored; silently resetting them would lose data, so
            // surface the corruption instead of self-healing.
            throw new InvalidOperationException(
                $"Profiles file is corrupt: {ProfilesFile}. Fix or delete it, then re-create profiles with 'tx profile set'.", ex);
        }

        return profiles is null
            ? NewProfileMap()
            : new Dictionary<string, CliProfile>(profiles, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveProfiles(IDictionary<string, CliProfile> profiles)
    {
        Directory.CreateDirectory(_configDirectory);
        AtomicFile.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, SerializerOptions));
    }

    public IReadOnlyList<RecentConnection> LoadRecentConnections()
    {
        if (!File.Exists(RecentConnectionsFile))
            return [];

        var json = File.ReadAllText(RecentConnectionsFile);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        List<RecentConnection>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RecentConnection>>(json);
        }
        catch (JsonException)
        {
            // The recents file is a convenience cache, not user data: a corrupt file
            // self-heals on the next AddRecentConnection instead of failing commands.
            return [];
        }

        return entries is null
            ? []
            : entries.Where(entry => entry?.Connection is not null && HasTarget(entry.Connection)).ToList();
    }

    public void AddRecentConnection(CliConnectionState state)
    {
        if (!HasTarget(state))
            return;

        var key = RecentKey(state);
        var entries = LoadRecentConnections()
            .Where(entry => !string.Equals(RecentKey(entry.Connection), key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        entries.Insert(0, new RecentConnection(state, DateTimeOffset.UtcNow));
        if (entries.Count > MaxRecentConnections)
            entries.RemoveRange(MaxRecentConnections, entries.Count - MaxRecentConnections);

        Directory.CreateDirectory(_configDirectory);
        AtomicFile.WriteAllText(RecentConnectionsFile, JsonSerializer.Serialize(entries, SerializerOptions));
    }

    internal static string RecentKey(CliConnectionState state)
        => !string.IsNullOrWhiteSpace(state.Model)
            ? $"model:{state.Model}"
            : $"remote:{state.Server}\0{state.Database}";

    private static bool HasTarget(CliConnectionState state)
        => !string.IsNullOrWhiteSpace(state.Model) || !string.IsNullOrWhiteSpace(state.Server);

    public CliConnectionState? LoadCurrentSession()
    {
        if (!File.Exists(CurrentSessionFile))
            return null;

        var json = File.ReadAllText(CurrentSessionFile);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CliConnectionState>(json);
        }
        catch (JsonException)
        {
            // A session is re-creatable with 'tx connect'; a corrupt file must not
            // brick every command, so treat it as "no active session".
            return null;
        }
    }

    public void SaveCurrentSession(CliConnectionState state)
    {
        Directory.CreateDirectory(SessionsDirectory);
        AtomicFile.WriteAllText(CurrentSessionFile, JsonSerializer.Serialize(state, SerializerOptions));
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
