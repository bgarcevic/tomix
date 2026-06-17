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

    public StagingStore()
        : this(TomixPaths.ConfigDirectory, new CliStateStore().CurrentSessionId)
    {
    }

    public StagingStore(string configDirectory, string sessionId)
    {
        _configDirectory = configDirectory;
        _sessionId = sessionId;
    }

    public string StagingRoot => Path.Combine(_configDirectory, "staging");

    public string SessionStagingDirectory => Path.Combine(StagingRoot, SafeFileName(_sessionId));

    /// <summary>Returns the existing working copy for <paramref name="source"/>, or materializes a fresh one.</summary>
    public async Task<StagingHandle> GetOrCreateAsync(
        ModelReference source,
        CliConnectionState? connection,
        IReadOnlyList<IModelProvider> providers,
        CancellationToken cancellationToken)
    {
        var existing = TryLoadManifest(source);
        if (existing is not null)
            return new StagingHandle(this, ManifestFile(source), existing);

        var serialization = ResolveSerialization(source, connection);
        var modelDirectory = ModelDirectory(source);
        var workingRoot = Path.Combine(modelDirectory, "working");
        Directory.CreateDirectory(workingRoot);

        var provider = providers.FirstOrDefault(p => p.CanOpen(source))
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
        return new StagingHandle(this, ManifestFile(source), manifest);
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
        File.WriteAllText(ManifestFile(source), JsonSerializer.Serialize(manifest, SerializerOptions));
    }

    internal void WriteManifest(string manifestFile, StagingManifest manifest)
        => File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest, SerializerOptions));

    private StagingManifest? TryLoadManifest(ModelReference source)
    {
        var file = ManifestFile(source);
        return File.Exists(file) ? ReadManifest(file) : null;
    }

    private static StagingManifest? ReadManifest(string manifestFile)
    {
        var json = File.ReadAllText(manifestFile);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<StagingManifest>(json);
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

/// <summary>A live handle to a materialized working copy; records staged operations into its manifest.</summary>
public sealed class StagingHandle
{
    private readonly StagingStore _store;
    private readonly string _manifestFile;

    internal StagingHandle(StagingStore store, string manifestFile, StagingManifest manifest)
    {
        _store = store;
        _manifestFile = manifestFile;
        Manifest = manifest;
    }

    public StagingManifest Manifest { get; private set; }

    /// <summary>The model reference to open and mutate (the working copy on disk).</summary>
    public ModelReference WorkingCopyReference => new(Manifest.WorkingCopy);

    public Task AppendOpAsync(string command, string summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var op = new StagedOp(Manifest.Ops.Count + 1, DateTimeOffset.UtcNow, command, summary);
        Manifest = Manifest with
        {
            UpdatedUtc = op.Utc,
            Ops = [.. Manifest.Ops, op]
        };
        _store.WriteManifest(_manifestFile, Manifest);
        return Task.CompletedTask;
    }
}
