using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomix.Core.Configuration;
using Tomix.Core.Models;

namespace Tomix.App.State;

/// <summary>
/// Persists per-session "staged" working copies of a model under the config directory so that
/// <c>--stage</c> mutations accumulate across separate CLI process invocations. Mirrors
/// <see cref="CliStateStore"/>'s config-dir + session-id conventions, and lives beside
/// <c>sessions/</c> so <c>TOMIX_CONFIG_DIR</c> isolates it in tests.
/// <code>
/// &lt;configDir&gt;/staging/&lt;sessionId&gt;/&lt;modelKey&gt;/
///     manifest.json   (StagingManifest)
///     working/        (the materialized model: tmdl folder, or model.bim)
/// </code>
/// </summary>
public sealed class StagingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configDirectory;
    private readonly string _sessionId;

    public StagingStore(string configDirectory, string sessionId)
    {
        _configDirectory = configDirectory;
        _sessionId = sessionId;
    }

    public string StagingRoot => Path.Combine(_configDirectory, "staging");

    public string SessionStagingDirectory => Path.Combine(StagingRoot, SafeFileName(_sessionId));

    /// <summary>
    /// Returns the existing working copy for <paramref name="source"/>, or materializes a fresh one.
    /// The returned handle OWNS the per-model lock until disposed, so a concurrent invocation cannot
    /// interleave its working-copy save/append with this one and lose staged operations.
    /// </summary>
    public async Task<StagingHandle> GetOrCreateAsync(
        ModelReference source,
        CliConnectionState? connection,
        IReadOnlyList<IModelProvider> providers,
        CancellationToken cancellationToken)
    {
        var modelLock = AcquireModelLock(ModelDirectory(source));
        try
        {
            var existing = TryLoadManifest(source);
            if (existing is not null)
                return new StagingHandle(this, ManifestFile(source), existing, modelLock);

            var serialization = ResolveSerialization(source, connection);
            var modelDirectory = ModelDirectory(source);
            var workingRoot = Path.Combine(modelDirectory, "working");
            Directory.CreateDirectory(workingRoot);

            var provider = providers.ResolveSingle(source)
                ?? throw new InvalidOperationException($"No provider can open model: {source.Value}");

            await using var session = await provider.OpenAsync(source, cancellationToken);
            if (session is not IModelExportSession exporter)
                throw new NotSupportedException($"Provider cannot materialize a working copy for: {source.Value}");

            var workingTarget = serialization == "bim"
                ? Path.Combine(workingRoot, "model.bim")
                : workingRoot;

            var export = await exporter.ExportAsync(
                new ModelExportRequest(workingTarget, serialization, Force: true, SupportingFiles: false),
                cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var manifest = new StagingManifest(
                SessionId: _sessionId,
                Source: CanonicalSource(source),
                SourceKind: source.IsRemote ? "remote" : "local",
                SourceEndpoint: source.IsRemote ? source.Value : null,
                SourceDatabase: source.IsRemote ? source.Database : null,
                Workspace: ResolveWorkspace(connection),
                Serialization: serialization,
                WorkingCopy: export.SavedPath,
                CreatedUtc: now,
                UpdatedUtc: now,
                SourceFingerprint: ComputeFingerprint(source),
                Ops: []);

            WriteManifest(source, manifest);
            return new StagingHandle(this, ManifestFile(source), manifest, modelLock);
        }
        catch
        {
            modelLock.Dispose();
            throw;
        }
    }

    public StagingInfo? TryLoad(ModelReference source)
    {
        var manifest = TryLoadManifest(source);
        return manifest is null ? null : new StagingInfo(manifest, IsCurrentSession: true);
    }

    public IReadOnlyList<StagingInfo> List()
    {
        if (!Directory.Exists(SessionStagingDirectory))
            return [];

        var infos = new List<StagingInfo>();
        foreach (var modelDir in Directory.EnumerateDirectories(SessionStagingDirectory))
        {
            var manifestFile = Path.Combine(modelDir, "manifest.json");
            if (!File.Exists(manifestFile))
                continue;

            var manifest = ReadManifest(manifestFile);
            if (manifest is not null)
                infos.Add(new StagingInfo(manifest, IsCurrentSession: true));
        }

        return infos.OrderBy(i => i.Manifest.Source, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Discard(ModelReference source)
    {
        var modelDirectory = ModelDirectory(source);
        if (!Directory.Exists(modelDirectory))
            return false;

        using var _ = AcquireModelLock(modelDirectory);
        Directory.Delete(modelDirectory, recursive: true);
        return true;
    }

    public int DiscardAll()
    {
        if (!Directory.Exists(SessionStagingDirectory))
            return 0;

        var count = 0;
        foreach (var modelDir in Directory.EnumerateDirectories(SessionStagingDirectory))
        {
            using var _ = AcquireModelLock(modelDir);
            Directory.Delete(modelDir, recursive: true);
            count++;
        }

        return count;
    }

    /// <summary>Recomputes the source fingerprint and compares it to the staged manifest's.</summary>
    public bool HasSourceDrifted(StagingManifest manifest, ModelReference source)
        => manifest.SourceFingerprint is not null
           && !string.Equals(manifest.SourceFingerprint, ComputeFingerprint(source), StringComparison.Ordinal);

    internal void WriteManifest(ModelReference source, StagingManifest manifest)
    {
        Directory.CreateDirectory(ModelDirectory(source));
        AtomicFile.WriteAllText(ManifestFile(source), JsonSerializer.Serialize(manifest, SerializerOptions));
    }

    internal void WriteManifest(string manifestFile, StagingManifest manifest)
        => AtomicFile.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest, SerializerOptions));

    private StagingManifest? TryLoadManifest(ModelReference source)
    {
        var file = ManifestFile(source);
        return File.Exists(file) ? ReadManifest(file) : null;
    }

    private static StagingManifest? ReadManifest(string manifestFile)
    {
        var json = File.ReadAllText(manifestFile);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StagingManifest>(json);
        }
        catch (JsonException ex)
        {
            // Staged work is user data; never silently reset it. The caller surfaces
            // TOMIX_STAGE_MANIFEST_CORRUPT with the discard recovery path.
            throw new StagingManifestCorruptException(manifestFile, ex);
        }
    }

    /// <summary>
    /// Takes an exclusive cross-process lock for one staged model so concurrent tx invocations
    /// cannot interleave materialize/mutate/append/discard and lose staged operations. A staged
    /// mutation holds the lock for its entire lifetime (via <see cref="StagingHandle"/>), because
    /// releasing between materialize and append would let a second invocation save its working
    /// copy and manifest from stale state, silently dropping the first invocation's change.
    /// Config and session files stay unlocked on purpose: they are single-value last-writer-wins
    /// state. The lock file sits beside (not inside) the model directory so Discard can delete
    /// the directory while holding the lock, and is removed on release.
    /// </summary>
    internal static IDisposable AcquireModelLock(string modelDirectory)
    {
        var lockFile = modelDirectory + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(
                    lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    $"Another tx process is working on this staged model (lock: {lockFile}). Retry when it finishes.");
            }
        }
    }

    private string ManifestFile(ModelReference source) => Path.Combine(ModelDirectory(source), "manifest.json");

    private string ModelDirectory(ModelReference source)
        => Path.Combine(SessionStagingDirectory, ModelKey(source));

    private static StagingWorkspace? ResolveWorkspace(CliConnectionState? connection)
        => string.IsNullOrWhiteSpace(connection?.Workspace)
            ? null
            : new StagingWorkspace(connection!.Workspace!, connection.Database);

    private static string ResolveSerialization(ModelReference source, CliConnectionState? connection)
    {
        if (!string.IsNullOrWhiteSpace(connection?.WorkspaceFormat))
            return NormalizeFormat(connection!.WorkspaceFormat!);

        var extension = Path.GetExtension(source.Value);
        return extension.Equals(".bim", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tmsl", StringComparison.OrdinalIgnoreCase)
            ? "bim"
            : "tmdl";
    }

    private static string NormalizeFormat(string format)
        => format.Trim().Equals("bim", StringComparison.OrdinalIgnoreCase) ? "bim" : "tmdl";

    private static string CanonicalSource(ModelReference source)
        => source.IsRemote
            ? $"{source.Value}|{source.Database}"
            : Path.GetFullPath(source.Value);

    private static string ModelKey(ModelReference source)
    {
        var canonical = source.IsRemote ? CanonicalSource(source) : CanonicalSource(source).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string? ComputeFingerprint(ModelReference source)
    {
        if (source.IsRemote)
            return null;

        var path = Path.GetFullPath(source.Value);
        var builder = new StringBuilder();

        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder.Append(Path.GetRelativePath(path, file))
                    .Append('|').Append(info.Length)
                    .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                    .Append('\n');
            }
        }
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
        }
        else
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SafeFileName(string value)
    {
        var safe = value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe;
    }
}

/// <summary>Thrown when a staged manifest exists but no longer parses (torn write, manual edit).</summary>
public sealed class StagingManifestCorruptException : InvalidOperationException
{
    public StagingManifestCorruptException(string manifestFile, Exception inner)
        : base($"Staged manifest is corrupt: {manifestFile}. Run 'tx stage discard' to reset staging for this model.", inner)
    {
    }
}

public sealed record StagingManifest(
    string SessionId,
    string Source,
    string SourceKind,
    string? SourceEndpoint,
    string? SourceDatabase,
    StagingWorkspace? Workspace,
    string Serialization,
    string WorkingCopy,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? SourceFingerprint,
    IReadOnlyList<StagedOp> Ops);

public sealed record StagingWorkspace(string Server, string? Database);

public sealed record StagedOp(int Seq, DateTimeOffset Utc, string Command, string Summary);

public sealed record StagingInfo(StagingManifest Manifest, bool IsCurrentSession);

/// <summary>
/// A live handle to a materialized working copy; records staged operations into its manifest.
/// Holds the per-model lock from materialization until disposed so a concurrent invocation's
/// working-copy save/append cannot interleave with (and silently overwrite) this one's.
/// </summary>
public sealed class StagingHandle : IDisposable
{
    private readonly StagingStore _store;
    private readonly string _manifestFile;
    private readonly IDisposable _modelLock;

    internal StagingHandle(StagingStore store, string manifestFile, StagingManifest manifest, IDisposable modelLock)
    {
        _store = store;
        _manifestFile = manifestFile;
        _modelLock = modelLock;
        Manifest = manifest;
    }

    public StagingManifest Manifest { get; private set; }

    /// <summary>The model reference to open and mutate (the working copy on disk).</summary>
    public ModelReference WorkingCopyReference => new(Manifest.WorkingCopy);

    public Task AppendOpAsync(string command, string summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No lock acquisition here: this handle has held the model lock since materialization,
        // so the on-disk manifest cannot have changed underneath the in-memory copy.
        var op = new StagedOp(Manifest.Ops.Count + 1, DateTimeOffset.UtcNow, command, summary);
        Manifest = Manifest with
        {
            UpdatedUtc = op.Utc,
            Ops = [.. Manifest.Ops, op]
        };
        _store.WriteManifest(_manifestFile, Manifest);
        return Task.CompletedTask;
    }

    public void Dispose() => _modelLock.Dispose();
}
