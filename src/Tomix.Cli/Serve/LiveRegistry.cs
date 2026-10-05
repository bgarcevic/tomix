using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.Platform.Configuration;

namespace Tomix.Cli.Serve;

/// <summary>A live session another process can join: where it listens and the token it needs.</summary>
/// <param name="Model">The model it has open, as <c>session.status</c> reports it.</param>
/// <param name="ProcessId">The process holding the session.</param>
/// <param name="Port">Its port on <c>127.0.0.1</c>.</param>
/// <param name="Token">The session token every request must carry.</param>
/// <param name="StartedAt">When it started, UTC.</param>
internal sealed record LiveEntry(string Model, int ProcessId, int Port, string Token, DateTimeOffset StartedAt)
{
    public Uri BaseUrl => new($"http://127.0.0.1:{Port}/");
}

/// <summary>
/// The per-user registry of live sessions (ADR 0001 §1, docs/protocol.md, Discovery): one JSON
/// file per open model in <c>~/.tomix/live</c>, named by a hash of the model, so a second
/// <c>tx ui</c>, <c>tx serve</c> or a status-bar tool finds the session already open. An entry
/// whose process has exited is stale and removed when found.
/// </summary>
internal sealed class LiveRegistry(string directory, Func<int, bool>? isRunning = null)
{
    private readonly Func<int, bool> _isRunning = isRunning ?? IsRunning;

    public static LiveRegistry Default => new(TomixPaths.LiveDirectory);

    /// <summary>Records <paramref name="entry"/> until the result is disposed.</summary>
    public IDisposable Register(LiveEntry entry)
    {
        CreateDirectory();
        var path = PathFor(entry.Model);
        var json = new JsonObject
        {
            ["model"] = entry.Model,
            ["pid"] = entry.ProcessId,
            ["port"] = entry.Port,
            ["url"] = entry.BaseUrl.ToString(),
            ["token"] = entry.Token,
            ["startedAt"] = entry.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
        };
        AtomicFile.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new Removal(() => Remove(path, entry.ProcessId));
    }

    /// <summary>The live session that has <paramref name="model"/> open, if its process is still running.</summary>
    public LiveEntry? Find(string model)
    {
        var path = PathFor(model);
        var entry = Read(path);
        if (entry is null)
            return null;
        if (_isRunning(entry.ProcessId) && SameModel(entry.Model, model))
            return entry;

        TryDelete(path);
        return null;
    }

    /// <summary>Every live session, stale entries removed.</summary>
    public IReadOnlyList<LiveEntry> All()
    {
        if (!Directory.Exists(directory))
            return [];

        var entries = new List<LiveEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (Read(path) is not { } entry)
                continue;
            if (_isRunning(entry.ProcessId))
                entries.Add(entry);
            else
                TryDelete(path);
        }

        return entries;
    }

    /// <summary>
    /// The registry file for <paramref name="model"/>: <c>&lt;leaf&gt;-&lt;hash&gt;.json</c>. The
    /// leaf keeps the folder readable; the hash of the normalised model keeps two models apart.
    /// </summary>
    internal string PathFor(string model)
    {
        var key = Key(model);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        var leaf = new StringBuilder();
        foreach (var character in Path.GetFileName(Path.TrimEndingDirectorySeparator(model)).ToLowerInvariant())
            leaf.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        return Path.Combine(directory, $"{(leaf.Length == 0 ? "model" : leaf.ToString())}-{hash}.json");
    }

    /// <summary>A local path made absolute and, on Windows, case-folded; anything else as given.</summary>
    private static string Key(string model)
    {
        if (model.Contains("://", StringComparison.Ordinal))
            return model;
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(model));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return model;
        }

        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static bool SameModel(string registered, string model) => Key(registered) == Key(model);

    private static LiveEntry? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
                return null;
            return new LiveEntry(
                (string?)json["model"] ?? "",
                (int?)json["pid"] ?? 0,
                (int?)json["port"] ?? 0,
                (string?)json["token"] ?? "",
                DateTimeOffset.TryParse((string?)json["startedAt"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var started) ? started : default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // Unreadable or half-written by an older tx: treated as absent.
            return null;
        }
    }

    private void Remove(string path, int processId)
    {
        // Only our own entry: another process may have registered the model since.
        if (Read(path) is { } current && current.ProcessId == processId)
            TryDelete(path);
    }

    private void CreateDirectory()
    {
        if (Directory.Exists(directory))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
