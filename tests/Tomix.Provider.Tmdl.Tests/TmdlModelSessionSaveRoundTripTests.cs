using Tomix.Core.Models;

namespace Tomix.Provider.Tmdl.Tests;

/// <summary>
/// Save round-trips on a temp copy of <c>samples/basic-tmdl</c>: an unmodified in-place save must
/// re-open with the same summary, and a mutation must survive save + re-open. Force/overwrite
/// semantics are pinned separately in Tomix.Provider.Tom.Tests/SaveAsyncForceTests.
/// </summary>
public sealed class TmdlModelSessionSaveRoundTripTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task SaveAsync_UnmodifiedInPlace_ReopensWithSameSummary()
    {
        var modelPath = CopySample();

        ModelSummary before;
        await using (var session = new TmdlModelSession(modelPath))
        {
            before = await session.GetSummaryAsync(CancellationToken.None);
            var result = await session.SaveAsync(
                outputPath: null, "tmdl", force: false, CancellationToken.None);
            Assert.Equal(modelPath, result.SavedPath);
        }

        await using var reopened = new TmdlModelSession(modelPath);
        var after = await reopened.GetSummaryAsync(CancellationToken.None);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SetProperty_MeasureDescription_PersistsAfterSaveAndReopen()
    {
        var modelPath = CopySample();
        const string description = "Round-trip description set by test.";

        await using (var session = new TmdlModelSession(modelPath))
        {
            var mutation = session.SetProperty(new ModelObjectSetRequest(
                "'Sales'[Total Sales]",
                [new ModelPropertyAssignment("description", description)],
                Type: null));
            Assert.True(mutation.Changed);

            await session.SaveAsync(outputPath: null, "tmdl", force: false, CancellationToken.None);
        }

        await using var reopened = new TmdlModelSession(modelPath);
        var snapshot = await reopened.GetSnapshotAsync(CancellationToken.None);
        var sales = Assert.Single(snapshot.Objects, o =>
            o.Kind == ModelObjectKind.Table && o.Name == "Sales");
        var measure = Assert.Single(sales.Children, c =>
            c.Kind == ModelObjectKind.Measure && c.Name == "Total Sales");
        Assert.Equal(description, measure.Description);
    }

    [Fact]
    public async Task SaveAsync_ToNewFolder_ReopensWithSameSummary()
    {
        var modelPath = CopySample();
        var targetPath = Path.Combine(_tempDir.Path, "saved-copy");

        ModelSummary before;
        await using (var session = new TmdlModelSession(modelPath))
        {
            before = await session.GetSummaryAsync(CancellationToken.None);
            var result = await session.SaveAsync(
                targetPath, "tmdl", force: false, CancellationToken.None);
            Assert.Equal(targetPath, result.SavedPath);
        }

        Assert.True(new TmdlModelProvider().CanOpen(new ModelReference(targetPath)));

        await using var reopened = new TmdlModelSession(targetPath);
        var after = await reopened.GetSummaryAsync(CancellationToken.None);
        Assert.Equal(before, after);
    }

    private string CopySample()
    {
        var destination = Path.Combine(_tempDir.Path, "basic-tmdl");
        TestSupport.CopyDirectory(TestSupport.SampleTmdlFolder, destination);
        return destination;
    }
}
