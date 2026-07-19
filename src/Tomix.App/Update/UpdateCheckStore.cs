using System.Text.Json;
using Tomix.Platform.Configuration;

namespace Tomix.App.Update;

/// <summary>
/// Caches the latest known release version in <c>update-check.json</c> so the end-of-command
/// notice never waits on the network. The file is a convenience cache: corruption self-heals
/// on the next successful check (same policy as the recent-connections file).
/// </summary>
public sealed class UpdateCheckStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configDirectory;
    private readonly TimeProvider _clock;

    public UpdateCheckStore(string configDirectory, TimeProvider? clock = null)
    {
        _configDirectory = configDirectory;
        _clock = clock ?? TimeProvider.System;
    }

    public string FilePath => Path.Combine(_configDirectory, "update-check.json");

    public UpdateCheckState? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var state = JsonSerializer.Deserialize<UpdateCheckState>(json);
            return string.IsNullOrWhiteSpace(state?.LatestVersion) ? null : state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string latestVersion)
    {
        Directory.CreateDirectory(_configDirectory);
        var state = new UpdateCheckState(_clock.GetUtcNow(), latestVersion);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, SerializerOptions));
    }

    public bool IsStale(TimeSpan ttl)
    {
        var state = Load();
        return state is null || _clock.GetUtcNow() - state.LastCheckedUtc > ttl;
    }
}

public sealed record UpdateCheckState(DateTimeOffset LastCheckedUtc, string LatestVersion);
