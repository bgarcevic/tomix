using Tomix.App.Summary;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Tests;

public sealed class SummaryModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReportsSettingsAndCounts()
    {
        var result = await new SummaryModelHandler([new StubModelProvider()]).HandleAsync(
            new SummaryModelRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Sales", "Sales")),
            CancellationToken.None);

        Assert.True(result.Success);
        var summary = result.Data!;
        Assert.Equal("stub", summary.Name);
        Assert.Equal("Sales", summary.Database);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/Sales", summary.Source);
        Assert.Null(summary.Format);
        Assert.Equal(1601, summary.CompatibilityLevel);
        Assert.Equal("en-US", summary.Culture);
        Assert.Equal("Import", summary.DefaultMode);
        Assert.Equal(
            new SummaryCounts(
                Tables: 2, Columns: 12, Measures: 4, Relationships: 1, Roles: 0,
                Partitions: 3, CalculationGroups: 1, Perspectives: 1, Cultures: 2),
            summary.Counts);
    }

    [Theory]
    [InlineData("model.bim", "bim")]
    [InlineData("model.tmsl", "bim")]
    [InlineData("Sales.SemanticModel/definition", "tmdl")]
    public async Task HandleAsync_LocalPath_ReportsFullPathAndFormat(string path, string format)
    {
        var result = await new SummaryModelHandler([new StubModelProvider()]).HandleAsync(
            new SummaryModelRequest(new ModelReference(path)),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(path), result.Data!.Source);
        Assert.Equal(format, result.Data.Format);
        Assert.Null(result.Data.Database);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var result = await new SummaryModelHandler([]).HandleAsync(
            new SummaryModelRequest(new ModelReference("any")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    private static ModelObject Obj(string name, ModelObjectKind kind, params ModelObject[] children)
        => new(name, kind, name, Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, children);

    private sealed class StubModelProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession());
    }

    private sealed class StubSession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 2, 12, 4, 1, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot(
                "stub",
                1601,
                [
                    Obj("Sales", ModelObjectKind.Table,
                        Obj("Sales-2025", ModelObjectKind.Partition),
                        Obj("Sales-2026", ModelObjectKind.Partition)),
                    Obj("Time Intelligence", ModelObjectKind.Table,
                        Obj("Time Intelligence", ModelObjectKind.Partition),
                        Obj("YTD", ModelObjectKind.CalculationItem),
                        Obj("PY", ModelObjectKind.CalculationItem)),
                    Obj("Executive", ModelObjectKind.Perspective),
                    Obj("en-US", ModelObjectKind.Culture),
                    Obj("da-DK", ModelObjectKind.Culture),
                ],
                new Dictionary<string, string>
                {
                    [PropertyBagKeys.Culture] = "en-US",
                    [PropertyBagKeys.DefaultMode] = "Import",
                }));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
