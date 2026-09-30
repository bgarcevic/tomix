using System.Text.Json;
using Tomix.Platform.Configuration;

namespace Tomix.App.State;

public sealed class CliStateStore
{
    private readonly string _configDirectory;
    private readonly Lazy<(string Id, string? Scope)> _session;

    /// <param name="currentSessionId">An explicit session id; overrides every other source.</param>
    /// <param name="workingDirectory">
    /// Where to resolve the directory scope from when no session is named; defaults to the
    /// process working directory. Injectable so tests do not depend on where they run.
    /// </param>
    public CliStateStore(string configDirectory, string? currentSessionId = null, string? workingDirectory = null)
    {
        _configDirectory = configDirectory;

        // Resolved once: a store must keep addressing the same session file even if the process
        // working directory changes mid-command.
        _session = new Lazy<(string, string?)>(() => ResolveSession(currentSessionId, workingDirectory));
    }

    public const int MaxRecentConnections = 20;

    public string ProfilesFile => Path.Combine(_configDirectory, "profiles.json");

    public string RecentConnectionsFile => Path.Combine(_configDirectory, "recent-connections.json");

    public string SessionsDirectory => Path.Combine(_configDirectory, "sessions");

    /// <summary>
    /// The session this process reads and writes. Precedence: an explicit id, then
    /// <c>TOMIX_SESSION</c> (or legacy <c>TE_SESSION</c>), then the directory scope — the enclosing
    /// git repository or worktree root, else the working directory (<see cref="SessionScope"/>).
    /// </summary>
    public string CurrentSessionId => _session.Value.Id;

    /// <summary>
    /// The directory the current session is bound to, or null when the session is named
    /// explicitly and therefore not tied to a directory.
    /// </summary>
    public string? CurrentSessionScope => _session.Value.Scope;

    public string CurrentSessionKind
        => CurrentSessionScope is not null ? "directory"
            : CurrentSessionId.StartsWith("pid-", StringComparison.OrdinalIgnoreCase) ? "pid"
            : "named";

    private static (string Id, string? Scope) ResolveSession(string? explicitId, string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
            return (explicitId.Trim(), null);

        var named = Environment.GetEnvironmentVariable("TOMIX_SESSION");
        if (string.IsNullOrWhiteSpace(named))
            named = Environment.GetEnvironmentVariable("TE_SESSION");
        if (!string.IsNullOrWhiteSpace(named))
            return (named.Trim(), null);

        var scope = SessionScope.FindRoot(workingDirectory ?? Environment.CurrentDirectory);
        return (SessionScope.SessionIdFor(scope), scope);
    }

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
            profiles = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringCliProfile);
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
            : profiles.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    Local = pair.Value.Local || !string.IsNullOrWhiteSpace(pair.Value.Model)
                },
                StringComparer.OrdinalIgnoreCase);
    }

    public void SaveProfiles(IDictionary<string, CliProfile> profiles)
    {
        Directory.CreateDirectory(_configDirectory);
        AtomicFile.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, AppJsonContext.Default.DictionaryStringCliProfile));
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
            entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListRecentConnection);
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

        // Recents are reconnection targets, not display state: Desktop picks a new port on every
        // start, so a cached report name is worthless here and its port-file path would leak into
        // `connect --recent --output-format json`.
        entries.Insert(0, new RecentConnection(state.ToPublic(), DateTimeOffset.UtcNow));
        if (entries.Count > MaxRecentConnections)
            entries.RemoveRange(MaxRecentConnections, entries.Count - MaxRecentConnections);

        Directory.CreateDirectory(_configDirectory);
        AtomicFile.WriteAllText(RecentConnectionsFile, JsonSerializer.Serialize(entries, AppJsonContext.Default.ListRecentConnection));
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
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.CliConnectionState);
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
        state = state with { Scope = CurrentSessionScope };
        AtomicFile.WriteAllText(CurrentSessionFile, JsonSerializer.Serialize(state, AppJsonContext.Default.CliConnectionState));
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

    /// <summary>
    /// Session files that provably belong to nothing any more: a directory session whose folder
    /// is gone (a deleted worktree or clone), or a legacy <c>pid-&lt;n&gt;</c> session whose
    /// process has exited. The current session is never stale. A directory session written
    /// before the scope was recorded cannot be judged, so it is kept.
    /// </summary>
    public IReadOnlyList<SessionFileInfo> SelectStaleSessions()
        => ListSessions()
            .Where(session => !session.Current && (IsDeadPidSession(session.SessionId) || IsOrphanedDirectorySession(session.Path)))
            .ToList();

    /// <summary>
    /// Deletes <see cref="SelectStaleSessions"/>. Best-effort housekeeping: a file that cannot be
    /// deleted (locked, already gone) is skipped rather than failing the caller.
    /// </summary>
    public int PruneStaleSessions()
    {
        try
        {
            return DeleteSessionFiles(SelectStaleSessions());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes every session file, the current one included. Returns how many were removed.</summary>
    public int ClearAllSessions()
        => DeleteSessionFiles(ListSessions());

    private static int DeleteSessionFiles(IReadOnlyList<SessionFileInfo> sessions)
    {
        var removed = 0;
        foreach (var session in sessions)
        {
            try
            {
                File.Delete(session.Path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static bool IsOrphanedDirectorySession(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.CliConnectionState);
            return !string.IsNullOrWhiteSpace(state?.Scope) && !Directory.Exists(state.Scope);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as orphaned; doctor reports corrupt files instead.
            return false;
        }
    }

    private static bool IsDeadPidSession(string sessionId)
    {
        if (!sessionId.StartsWith("pid-", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = sessionId["pid-".Length..];
        if (suffix.Length == 0 || suffix.Any(character => !char.IsAsciiDigit(character)) ||
            !int.TryParse(suffix, out var pid) || pid <= 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
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
