using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Force/overwrite semantics for <see cref="TomFileModelSession"/>'s save: an existing target is
/// refused without <c>--force</c>, and an in-place save of the source is never treated as an
/// existing target. The matching <c>TmdlModelSession</c> cases live in
/// Tomix.Provider.Tmdl.Tests/TmdlModelSessionSaveForceTests, beside the type they exercise.
/// </summary>
public sealed class SaveAsyncForceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TomFileModelSession_SaveAsync_ForcesExistingBimOnlyWhenForceIsTrue(bool force)
    {
        using var tempDir = new TempDir();
        var sourcePath = CopySampleBim(tempDir);
        var targetPath = tempDir.Combine("out.bim");
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
        var targetPath = tempDir.CreateSubdirectory("out");
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
        => SampleModel.CopyFileTo(tempDir, "source.bim", "basic-tmdl.bim");
}
