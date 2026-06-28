using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Provider.Tom.Tests;

public sealed class SaveAsyncForceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TomFileModelSession_SaveAsync_ForcesExistingBimOnlyWhenForceIsTrue(bool force)
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleBim(tempDir);
        var targetPath = Path.Combine(tempDir.Path, "out.bim");
        File.WriteAllText(targetPath, "{}");

        await using var session = new TomFileModelSession(sourcePath, null);

        if (force)
        {
            var result = await session.SaveAsync(targetPath, "bim", force, CancellationToken.None);
            Assert.Equal(targetPath, result.SavedPath);
        }
        else
        {
            await Assert.ThrowsAsync<OutputExistsException>(() =>
                session.SaveAsync(targetPath, "bim", force, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TomFileModelSession_SaveAsync_ForcesExistingTmdlOnlyWhenForceIsTrue(bool force)
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleBim(tempDir);
        var targetPath = Path.Combine(tempDir.Path, "out");
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Combine(targetPath, "database.tmdl"), "namespace foo");

        await using var session = new TomFileModelSession(sourcePath, null);

        if (force)
        {
            var result = await session.SaveAsync(targetPath, "tmdl", force, CancellationToken.None);
            Assert.Equal(targetPath, result.SavedPath);
        }
        else
        {
            await Assert.ThrowsAsync<OutputExistsException>(() =>
                session.SaveAsync(targetPath, "tmdl", force, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TmdlModelSession_SaveAsync_ForcesExistingTargetOnlyWhenForceIsTrue(bool force)
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleTmdl(tempDir);
        var targetPath = Path.Combine(tempDir.Path, "out");
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Combine(targetPath, "database.tmdl"), "namespace foo");

        await using var session = new TmdlModelSession(sourcePath);

        if (force)
        {
            var result = await session.SaveAsync(targetPath, "tmdl", force, CancellationToken.None);
            Assert.Equal(targetPath, result.SavedPath);
        }
        else
        {
            await Assert.ThrowsAsync<OutputExistsException>(() =>
                session.SaveAsync(targetPath, "tmdl", force, CancellationToken.None));
        }
    }

    [Fact]
    public async Task TmdlModelSession_SaveAsync_InPlaceOverwritesWithoutForce()
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleTmdl(tempDir);

        await using var session = new TmdlModelSession(sourcePath);

        // In-place save (null output) must succeed even without --force, and must not throw
        // an OutputExistsException for the source directory.
        var result = await session.SaveAsync(outputPath: null, "tmdl", force: false, CancellationToken.None);
        Assert.Equal(sourcePath, result.SavedPath);
    }

    [Fact]
    public async Task TmdlModelSession_ExportAsync_InPlaceOverwritesWithoutForce()
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleTmdl(tempDir);

        await using var session = new TmdlModelSession(sourcePath);

        // ExportAsync to the source path (the tx save path) must succeed without force.
        var result = await session.ExportAsync(
            new ModelExportRequest(sourcePath, "tmdl", Force: false, SupportingFiles: false),
            CancellationToken.None);
        Assert.Equal(sourcePath, result.SavedPath);
    }

    [Fact]
    public async Task TmdlModelSession_SaveAsync_InPlaceClearsStaleFiles()
    {
        using var tempDir = new TempDir();
        var targetPath = Path.Combine(tempDir.Path, "out");
        Directory.CreateDirectory(targetPath);

        // Seed the directory with a non-TMDL junk file that the serializer would NOT overwrite
        // on its own. A proper in-place save must clear it so stale artifacts don't survive.
        var junkPath = Path.Combine(targetPath, "stale-artifact.txt");
        File.WriteAllText(junkPath, "old content");

        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        await TomModelExporter.ExportAsync(
            db,
            new ModelExportRequest(targetPath, "tmdl", Force: true, SupportingFiles: false),
            CancellationToken.None);

        Assert.False(File.Exists(junkPath), "stale file should be cleared when force=true");
    }

    [Fact]
    public async Task TmdlModelSession_SaveAsync_SaveToExisting_DistinctFromSource_ThrowsOutputExists()
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleTmdl(tempDir);
        var otherPath = Path.Combine(tempDir.Path, "other");
        Directory.CreateDirectory(otherPath);
        File.WriteAllText(Path.Combine(otherPath, "database.tmdl"), "namespace foo");

        await using var session = new TmdlModelSession(sourcePath);

        await Assert.ThrowsAsync<OutputExistsException>(() =>
            session.SaveAsync(otherPath, "tmdl", force: false, CancellationToken.None));
    }

    [Fact]
    public async Task TomFileModelSession_SaveAsync_InPlaceOverwritesWithoutForce()
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleBim(tempDir);

        await using var session = new TomFileModelSession(sourcePath, null);

        var result = await session.SaveAsync(outputPath: null, "bim", force: false, CancellationToken.None);
        Assert.Equal(sourcePath, result.SavedPath);
    }

    private static string CopySampleBim(TempDir tempDir)
    {
        var source = TestPath("basic-tmdl.bim");
        var dest = Path.Combine(tempDir.Path, "source.bim");
        File.Copy(source, dest);
        return dest;
    }

    private static string CopySampleTmdl(TempDir tempDir)
    {
        var sourceDir = TestPath("basic-tmdl");
        var destDir = Path.Combine(tempDir.Path, "source-tmdl");
        CopyDirectory(sourceDir, destDir);
        return destDir;
    }

    private static string TestPath(string relative) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", relative));

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tomix-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { }
        }
    }
}
