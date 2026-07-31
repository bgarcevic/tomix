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

    /// <summary>
    /// Snapshot paths are not globally unique: a table named "Alder" with a column "Alder" and
    /// the default partition "Alder" produces three distinct objects sharing the path
    /// "Alder/Alder". Diff must key by kind+path instead of crashing on the duplicate
    /// ("An item with the same key has already been added").
    /// </summary>
    [Fact]
    public async Task HandleAsync_ToleratesSameNamedSiblings_AndComparesLikeWithLike()
    {
        var changes = await Diff(
            SelfNamedTableSnapshot(columnHidden: false),
            SelfNamedTableSnapshot(columnHidden: true));

        var change = Assert.Single(changes);
        Assert.Equal("modified", change.Action);
        Assert.Equal("IsHidden", change.Path);
        Assert.Equal("Column/Alder/Alder", change.ObjectType);
    }

    private static ModelSnapshot SelfNamedTableSnapshot(bool columnHidden)
    {
        var column = new ModelObject(
            "Alder", ModelObjectKind.Column, "Alder/Alder",
            Detail: "int64", Expression: null, Description: null, Hidden: columnHidden,
            SourceColumn: "Alder", Children: []);
        var partition = new ModelObject(
            "Alder", ModelObjectKind.Partition, "Alder/Alder",
            Detail: "m", Expression: "let Source = Sql in Source", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var table = new ModelObject(
            "Alder", ModelObjectKind.Table, "Alder",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [column, partition]);

        return new ModelSnapshot("stub", 1601, [table]);
    }

    [Fact]
    public async Task HandleAsync_IgnoresMeasureDataType_WhenLiveTargetIsUnprocessed()
    {
        // A measure's data type is engine-computed at process time; source files carry none.
        // Against a live database, absent-vs-present is processing state, not an authored change
        // (both-present changes are still reported — see HandleAsync_ReportsCatalogDiffableChanges).
        var unprocessed = Snapshot(measureBag: new Dictionary<string, string>
        {
            [PropertyBagKeys.FormatString] = "#,0"
        });
        var processed = Snapshot(measureBag: new Dictionary<string, string>
        {
            [PropertyBagKeys.FormatString] = "#,0",
            [PropertyBagKeys.DataType] = "Decimal"
        });

        Assert.Empty(await DiffAgainstLiveTarget(unprocessed, processed));
        Assert.Empty(await DiffAgainstLiveTarget(processed, unprocessed));
    }

    /// <summary>
    /// The suppression is scoped to live targets: between two authored sources, a data type on
    /// only one side is an authored difference, and hiding it would report unequal models as
    /// identical.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ReportsMeasureDataType_BetweenTwoAuthoredSources()
    {
        var without = Snapshot(measureBag: new Dictionary<string, string>());
        var with = Snapshot(measureBag: new Dictionary<string, string>
        {
            [PropertyBagKeys.DataType] = "Decimal"
        });

        var change = Assert.Single(await Diff(without, with));
        Assert.Equal("DataType", change.Path);
        Assert.Equal("Decimal", change.NewValue);
    }

    [Fact]
    public async Task HandleAsync_IgnoresCalculatedTableColumns_PresentOnLiveTargetOnly()
    {
        // Calculated-table columns are materialized by the engine when the table's expression
        // is evaluated, so a processed database has them while the source files don't. Only
        // the authored (regular) column counts as a real addition.
        var changes = await DiffAgainstLiveTarget(
            CalcTableSnapshot(withEngineColumns: false),
            CalcTableSnapshot(withEngineColumns: true));

        var change = Assert.Single(changes);
        Assert.Equal("added", change.Action);
        Assert.Equal("PnL/Authored", change.Path);
    }

    [Fact]
    public async Task HandleAsync_ReportsCalculatedTableColumns_BetweenTwoAuthoredSources()
    {
        var changes = await Diff(
            CalcTableSnapshot(withEngineColumns: false),
            CalcTableSnapshot(withEngineColumns: true));

        Assert.Contains(changes, c => c.Action == "added" && c.Path == "PnL/Group");
        Assert.Contains(changes, c => c.Action == "added" && c.Path == "PnL/Authored");
    }

    [Fact]
    public async Task HandleAsync_ComparesCalculatedTableColumns_PresentOnBothSides()
    {
        var changes = await Diff(
            CalcTableSnapshot(withEngineColumns: true, engineColumnHidden: false),
            CalcTableSnapshot(withEngineColumns: true, engineColumnHidden: true));

        var change = Assert.Single(changes, c => c.ObjectType == "Column/PnL/Group");
        Assert.Equal("IsHidden", change.Path);
    }

    private static ModelSnapshot CalcTableSnapshot(bool withEngineColumns, bool engineColumnHidden = false)
    {
        var children = new List<ModelObject>();
        if (withEngineColumns)
        {
            children.Add(new ModelObject(
                "Group", ModelObjectKind.Column, "PnL/Group",
                Detail: "string", Expression: null, Description: null, Hidden: engineColumnHidden,
                SourceColumn: null, Children: [],
                Properties: new Dictionary<string, string>
                {
                    [PropertyBagKeys.ColumnType] = "CalculatedTableColumn"
                }));
            children.Add(new ModelObject(
                "Authored", ModelObjectKind.Column, "PnL/Authored",
                Detail: "string", Expression: null, Description: null, Hidden: false,
                SourceColumn: "Authored", Children: []));
        }

        var table = new ModelObject(
            "PnL", ModelObjectKind.Table, "PnL",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: children);

        return new ModelSnapshot("stub", 1601, [table]);
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

    private static Task<IReadOnlyList<DiffChange>> Diff(ModelSnapshot left, ModelSnapshot right)
        => Diff(left, right, new ModelReference("right"));

    /// <summary>Right side is a processed database, which is what enables engine-state suppression.</summary>
    private static Task<IReadOnlyList<DiffChange>> DiffAgainstLiveTarget(
        ModelSnapshot left, ModelSnapshot right)
        => Diff(left, right, ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "db"));

    private static async Task<IReadOnlyList<DiffChange>> Diff(
        ModelSnapshot left, ModelSnapshot right, ModelReference rightReference)
    {
        var handler = new DiffModelHandler([new SnapshotProvider(left, right)]);
        var result = await handler.HandleAsync(
            new DiffModelRequest(new ModelReference("left"), rightReference),
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
