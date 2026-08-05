using Tomix.Core.Models;

namespace Tomix.Provider.Tmdl.Tests;

/// <summary>
/// Force/overwrite semantics for <see cref="TmdlModelSession"/>'s save and export: an existing
/// target distinct from the source is refused without <c>--force</c>, while writing back to the
/// source itself always succeeds — <c>tx save</c> with no output path must not be blocked by the
/// model it just opened.
/// </summary>
/// <remarks>
/// These cases lived in Tomix.Provider.Tom.Tests/SaveAsyncForceTests, which
/// <see cref="TmdlModelSessionSaveRoundTripTests"/> already flagged as drift: they exercise a
/// Tmdl-provider type, so they belong in the project that owns it.
/// </remarks>
public sealed class TmdlModelSessionSaveForceTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_ForcesExistingTargetOnlyWhenForceIsTrue(bool force)
    {
        var sourcePath = CopySample();
        var targetPath = _tempDir.CreateSubdirectory("out");
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
    public async Task SaveAsync_InPlaceOverwritesWithoutForce()
    {
        var sourcePath = CopySample();

        await using var session = new TmdlModelSession(sourcePath);

        // In-place save (null output) must succeed even without --force, and must not throw
        // an OutputExistsException for the source directory.
        var result = await session.SaveAsync(outputPath: null, "tmdl", force: false, CancellationToken.None);
        Assert.Equal(sourcePath, result.SavedPath);
    }

    [Fact]
    public async Task ExportAsync_InPlaceOverwritesWithoutForce()
    {
        var sourcePath = CopySample();

        await using var session = new TmdlModelSession(sourcePath);

        // ExportAsync to the source path (the tx save path) must succeed without force.
        var result = await session.ExportAsync(
            new ModelExportRequest(sourcePath, "tmdl", Force: false, SupportingFiles: false),
            CancellationToken.None);
        Assert.Equal(sourcePath, result.SavedPath);
    }

    [Fact]
    public async Task SaveAsync_ToExistingTargetDistinctFromSource_ThrowsOutputExists()
    {
        var sourcePath = CopySample();
        var otherPath = _tempDir.CreateSubdirectory("other");
        File.WriteAllText(Path.Combine(otherPath, "database.tmdl"), "namespace foo");

        await using var session = new TmdlModelSession(sourcePath);

        await Assert.ThrowsAsync<OutputExistsException>(() =>
            session.SaveAsync(otherPath, "tmdl", force: false, CancellationToken.None));
    }

    private string CopySample() => SampleModel.CopyTo(_tempDir, "source-tmdl");
}
