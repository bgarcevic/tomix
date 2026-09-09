using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The snapshot distinguishes calculated from data columns so `--type calculatedcolumn` filters
/// precisely, while `column` (and the Columns path keyword) still match both kinds.
/// </summary>
public sealed class TomSnapshotColumnKindTests
{
    [Fact]
    public void Snapshot_AssignsCalculatedColumnKind_ToCalculatedColumns()
    {
        var snapshot = Snapshot();

        Assert.Equal(
            ModelObjectKind.CalculatedColumn,
            Assert.Single(snapshot.Objects[0].Children, c => c.Name == "Margin").Kind);
        Assert.Equal(
            ModelObjectKind.Column,
            Assert.Single(snapshot.Objects[0].Children, c => c.Name == "Qty").Kind);
    }

    [Fact]
    public void Selector_ColumnFilter_MatchesBothKinds()
    {
        var snapshot = Snapshot();

        Assert.Equal(
            ["Qty", "Margin"],
            ModelObjectSelector.Select(snapshot, "Sales/Columns", ModelObjectKind.Column).Select(o => o.Name));
    }

    [Fact]
    public void Selector_CalculatedColumnFilter_NarrowsToCalculatedColumns()
    {
        var snapshot = Snapshot();

        Assert.Equal(
            ["Margin"],
            ModelObjectSelector.Select(snapshot, "Sales", ModelObjectKind.CalculatedColumn).Select(o => o.Name));
    }

    private static ModelSnapshot Snapshot()
    {
        var db = TestModels.NewDatabase();
        var table = TestModels.NewTable("Sales", "Qty");
        table.Columns.Add(new CalculatedColumn
        {
            Name = "Margin",
            DataType = DataType.Double,
            Expression = "[Qty] * 0.1"
        });
        db.Model.Tables.Add(table);

        return TomModelSummarizer.Snapshot(db, "M");
    }
}
