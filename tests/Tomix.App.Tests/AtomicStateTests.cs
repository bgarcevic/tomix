using Tomix.App.Stage;
using Tomix.App.State;
using Tomix.Core.Configuration;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Crash-safety contract for shared state under ~/.tomix: writes are atomic (temp + rename),
/// re-creatable state self-heals when corrupt, and user data (profiles, staged work) surfaces
/// corruption with a recovery path instead of crashing or silently resetting.
/// </summary>
public sealed class AtomicStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-atomic-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AtomicFile_WritesAndOverwrites_WithoutLeavingTempFiles()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AtomicFile_WriteAllBytes_RoundTrips()
    {
        var path = Path.Combine(_dir, "blob.enc");
        byte[] payload = [1, 2, 3, 255];

        AtomicFile.WriteAllBytes(path, payload);

        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadCurrentSession_SelfHeals_WhenSessionFileIsCorrupt()
    {
        var store = new CliStateStore(_dir);
        Directory.CreateDirectory(store.SessionsDirectory);
        File.WriteAllText(store.CurrentSessionFile, "{ not json");

        Assert.Null(store.LoadCurrentSession());
    }

    [Fact]
    public void LoadProfiles_SurfacesCorruption_InsteadOfResettingUserData()
    {
        var store = new CliStateStore(_dir);
        File.WriteAllText(store.ProfilesFile, "{ not json");

        var ex = Assert.Throws<InvalidOperationException>(() => store.LoadProfiles());
        Assert.Contains(store.ProfilesFile, ex.Message);
    }

    [Fact]
    public void StagingStore_CorruptManifest_ThrowsWithDiscardHint()
    {
        var store = new StagingStore(_dir, "test-session");
        var source = new ModelReference(Path.Combine(_dir, "model"));

        // Plant a corrupt manifest at the store's expected location for this source.
        var modelDir = PlantManifest(store, source, "{ not json");

        var ex = Assert.Throws<StagingManifestCorruptException>(() => store.TryLoad(source));
        Assert.Contains("stage discard", ex.Message);
        Assert.Contains(modelDir, ex.Message);
    }

    [Fact]
    public void StageHandler_CorruptManifest_SurfacesDiagnosticFromEveryCommand()
    {
        var store = new StagingStore(_dir, "test-session");
        var source = new ModelReference(Path.Combine(_dir, "model"));
        PlantManifest(store, source, "{ not json");
        var handler = new StageHandler(store);

        var status = handler.Status(source);
        var list = handler.List();
        var commit = handler.CommitAsync(source, [], force: false, CancellationToken.None).Result;

        foreach (var diagnostics in new[] { status.Diagnostics, list.Diagnostics, commit.Diagnostics })
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("TOMIX_STAGE_MANIFEST_CORRUPT", diagnostic.Code);
            Assert.Contains("stage discard", diagnostic.Message);
        }
        Assert.Equal(2, status.ExitCode);
        Assert.Equal(2, list.ExitCode);
        Assert.Equal(2, commit.ExitCode);
    }

    [Fact]
    public async Task StagingHandle_OwnsModelLock_AppendDoesNotReacquire_DisposeReleases()
    {
        var store = new StagingStore(_dir, "test-session");
        var source = new ModelReference(Path.Combine(_dir, "model"));
        var modelDir = PlantManifest(
            store, source,
            """
            {"SessionId":"test-session","Source":"s","SourceKind":"local","SourceEndpoint":null,
             "SourceDatabase":null,"Workspace":null,"Serialization":"tmdl","WorkingCopy":"w",
             "CreatedUtc":"2026-01-01T00:00:00Z","UpdatedUtc":"2026-01-01T00:00:00Z",
             "SourceFingerprint":null,"Ops":[]}
            """);
        var manifestFile = Path.Combine(modelDir, "manifest.json");

        // The handle owns the lock for its lifetime; AppendOpAsync must not try to re-acquire
        // it (that would deadlock against the handle's own lock until the 5s deadline).
        var handle = new StagingHandle(
            store, manifestFile, store.TryLoad(source)!.Manifest, StagingStore.AcquireModelLock(modelDir));
        var append = Task.Run(() => handle.AppendOpAsync("set", "set x", CancellationToken.None));
        Assert.True(await Task.WhenAny(append, Task.Delay(2000)) == append, "AppendOpAsync deadlocked on its own model lock");
        Assert.Single(handle.Manifest.Ops);

        handle.Dispose();
        using (StagingStore.AcquireModelLock(modelDir))
        {
            // Lock is free again after the handle is disposed.
        }
    }

    [Fact]
    public async Task AcquireModelLock_Waits_UntilHolderReleases()
    {
        var modelDir = Path.Combine(_dir, "some-model-key");

        var first = StagingStore.AcquireModelLock(modelDir);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            first.Dispose();
        });

        // Blocks in the retry loop until the first holder releases, well inside the 5s deadline.
        using (StagingStore.AcquireModelLock(modelDir))
        {
        }

        await release;
    }

    // Mirrors StagingStore's model-key layout (sha256 of the canonical local path, first 16 hex
    // chars) so a manifest can be planted where TryLoad will find it.
    private static string PlantManifest(StagingStore store, ModelReference source, string contents)
    {
        var canonical = Path.GetFullPath(source.Value).ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        var key = Convert.ToHexString(hash)[..16].ToLowerInvariant();

        var modelDir = Path.Combine(store.SessionStagingDirectory, key);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "manifest.json"), contents);
        return modelDir;
    }
}
