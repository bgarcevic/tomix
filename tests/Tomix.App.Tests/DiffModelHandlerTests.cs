using Tomix.App.Diff;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Tests;

/// <summary>
/// Diff compares the fixed identity set (Name/Kind/Detail/Expression/Description/IsHidden) plus
/// every catalog property flagged Diffable, so bag-backed changes like formatString or dataType
/// are reported instead of silently ignored.
/// </summary>
public sealed class DiffModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReportsCatalogDiffableChanges()
    {
        var left = Snapshot(measureBag: new Dictionary<string, string>
        {
            [PropertyBagKeys.FormatString] = "#,0",
            [PropertyBagKeys.DataType] = "Int64"
        });
        var right = Snapshot(measureBag: new Dictionary<string, string>
        {
            [PropertyBagKeys.FormatString] = "0.0%",
            [PropertyBagKeys.DataType] = "Decimal"
        });

        var changes = await Diff(left, right);

        var formatString = Assert.Single(changes, c => c.Path == "FormatString");
        Assert.Equal("modified", formatString.Action);
        Assert.Equal("Measure/Sales/Total Sales", formatString.ObjectType);
        Assert.Equal("#,0", formatString.OldValue);
        Assert.Equal("0.0%", formatString.NewValue);

        var dataType = Assert.Single(changes, c => c.Path == "DataType");
        Assert.Equal("Int64", dataType.OldValue);
        Assert.Equal("Decimal", dataType.NewValue);
    }

    [Fact]
    public async Task HandleAsync_ReportsColumnDataTypeChangeOnce_ViaDetail()
    {
        // A column's Detail IS its data type; the catalog deliberately leaves column dataType
        // un-Diffable so the same edit is not reported as both "Detail" and "DataType".
        var changes = await Diff(
            ColumnSnapshot(dataType: "int64"),
            ColumnSnapshot(dataType: "double"));

        var change = Assert.Single(changes);
        Assert.Equal("Detail", change.Path);
        Assert.Equal("int64", change.OldValue);
        Assert.Equal("double", change.NewValue);
    }

    private static ModelSnapshot ColumnSnapshot(string dataType)
    {
        var column = new ModelObject(
            "Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: dataType, Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: [],
            Properties: new Dictionary<string, string> { [PropertyBagKeys.DataType] = dataType });
        var sales = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [column]);

        return new ModelSnapshot("stub", 1601, [sales]);
    }

    [Fact]
    public async Task HandleAsync_ReportsNoChanges_WhenBagsMatch()
    {
        var bag = new Dictionary<string, string> { [PropertyBagKeys.FormatString] = "#,0" };
        var changes = await Diff(Snapshot(bag), Snapshot(new Dictionary<string, string>(bag)));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task HandleAsync_RightHasNoProvider_DoesNotOpenLeft()
    {
        var provider = new LeftOnlyProvider(Snapshot(new Dictionary<string, string>()));
        var handler = new DiffModelHandler([provider]);

        var result = await handler.HandleAsync(
            new DiffModelRequest(new ModelReference("left"), new ModelReference("right")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
        Assert.Equal(0, provider.OpenCount);
    }

    private static async Task<IReadOnlyList<DiffChange>> Diff(ModelSnapshot left, ModelSnapshot right)
    {
        var handler = new DiffModelHandler([new SnapshotProvider(left, right)]);
        var result = await handler.HandleAsync(
            new DiffModelRequest(new ModelReference("left"), new ModelReference("right")),
            CancellationToken.None);

        Assert.True(result.Success);
        return result.Data!.Changes;
    }

    private static ModelSnapshot Snapshot(IReadOnlyDictionary<string, string> measureBag)
    {
        var measure = new ModelObject(
            "Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: [], Properties: measureBag);
        var sales = new ModelObject(
            "Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [measure]);

        return new ModelSnapshot("stub", 1601, [sales]);
    }

    private sealed class SnapshotProvider(ModelSnapshot left, ModelSnapshot right) : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new SnapshotSession(
                reference.Value == "left" ? left : right));
    }

    private sealed class LeftOnlyProvider(ModelSnapshot snapshot) : IModelProvider
    {
        public int OpenCount { get; private set; }

        public bool CanOpen(ModelReference reference) => reference.Value == "left";

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<IModelSession>(new SnapshotSession(snapshot));
        }
    }

    private sealed class SnapshotSession(ModelSnapshot snapshot) : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
