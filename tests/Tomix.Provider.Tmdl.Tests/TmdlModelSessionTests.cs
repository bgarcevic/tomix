using Tomix.Core.Models;

namespace Tomix.Provider.Tmdl.Tests;

/// <summary>
/// Summarize/snapshot behavior of <see cref="TmdlModelSession"/> against the checked-in
/// <c>samples/basic-tmdl</c> model (3 tables, 12 columns, 4 measures on Sales, 2 relationships),
/// plus the session's capability surface: a file-backed TMDL session supports export, mutation,
/// and deploy, but never live-server-only capabilities (query, refresh).
/// </summary>
public sealed class TmdlModelSessionTests
{
    [Fact]
    public async Task GetSummaryAsync_BasicTmdlSample_ReportsKnownCounts()
    {
        await using var session = new TmdlModelSession(TestSupport.SampleTmdlFolder);

        var summary = await session.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(1601, summary.CompatibilityLevel);
        Assert.Equal(3, summary.Tables);
        Assert.Equal(12, summary.Columns);
        Assert.Equal(4, summary.Measures);
        Assert.Equal(2, summary.Relationships);
        Assert.Equal(0, summary.Roles);
    }

    [Fact]
    public async Task GetSnapshotAsync_BasicTmdlSample_ContainsSampleTables()
    {
        await using var session = new TmdlModelSession(TestSupport.SampleTmdlFolder);

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        var tableNames = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.Table)
            .Select(o => o.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Customers", "Products", "Sales"], tableNames);
    }

    [Fact]
    public async Task GetSnapshotAsync_BasicTmdlSample_SalesTableHasFourMeasures()
    {
        await using var session = new TmdlModelSession(TestSupport.SampleTmdlFolder);

        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        var sales = Assert.Single(snapshot.Objects, o =>
            o.Kind == ModelObjectKind.Table && o.Name == "Sales");
        var measureNames = sales.Children
            .Where(c => c.Kind == ModelObjectKind.Measure)
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Avg Sale", "Distinct Customers", "Order Count", "Total Sales"], measureNames);
    }

    [Fact]
    public async Task GetSummaryAsync_InvalidTmdlFolder_ThrowsModelLoadException()
    {
        using var tempDir = new TempDir();
        File.WriteAllText(Path.Combine(tempDir.Path, "database.tmdl"), "not valid tmdl {{{");
        File.WriteAllText(Path.Combine(tempDir.Path, "model.tmdl"), "also not valid");

        await using var session = new TmdlModelSession(tempDir.Path);

        await Assert.ThrowsAsync<ModelLoadException>(() =>
            session.GetSummaryAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(typeof(IModelSession))]
    [InlineData(typeof(IModelExportSession))]
    [InlineData(typeof(IModelMutationSession))]
    [InlineData(typeof(IExpressionRewriteSession))]
    [InlineData(typeof(IRefreshPolicyMutationSession))]
    [InlineData(typeof(IModelDeploySession))]
    public void TmdlModelSession_ImplementsFileBackedCapability(Type capability)
        => Assert.True(capability.IsAssignableFrom(typeof(TmdlModelSession)),
            $"TmdlModelSession should implement {capability.Name}");

    [Theory]
    [InlineData(typeof(IModelQuerySession))]
    [InlineData(typeof(IModelRefreshSession))]
    [InlineData(typeof(IRefreshPolicyApplySession))]
    public void TmdlModelSession_DoesNotImplementLiveServerCapability(Type capability)
        => Assert.False(capability.IsAssignableFrom(typeof(TmdlModelSession)),
            $"a file-backed TMDL session must not claim {capability.Name}");
}
