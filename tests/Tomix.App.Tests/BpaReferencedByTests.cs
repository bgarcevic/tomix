using Tomix.App.Bpa;
using Tomix.App.Bpa.Model;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Regression tests for table-qualified <c>ReferencedBy</c>: a column referenced only via a
/// qualified <c>'Table'[Col]</c> must not mark same-named columns in *other* tables as referenced
/// (the bug that under-reported UNNECESSARY_COLUMNS).
/// </summary>
public sealed class BpaReferencedByTests
{
    private static ModelObject Column(string name, string table)
        => new(name, ModelObjectKind.Column, $"{table}/{name}",
            Detail: null, Expression: null, Description: null, Hidden: true, SourceColumn: name,
            Children: [], Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });

    private static ModelObject Measure(string name, string table, string expression)
        => new(name, ModelObjectKind.Measure, $"{table}/{name}",
            Detail: null, Expression: expression, Description: null, Hidden: false, SourceColumn: null,
            Children: [], Properties: new Dictionary<string, string> { ["ObjectType"] = "Measure" });

    private static ModelObject Table(string name, params ModelObject[] children)
        => new(name, ModelObjectKind.Table, name,
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: children, Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });

    private static BpaColumn Col(BpaModel model, string table, string name)
        => model.AllColumns.Single(c => c.Table.Name == table && c.Name == name);

    [Fact]
    public void QualifiedReference_DoesNotMarkSameNamedColumnInAnotherTable()
    {
        // 'A'[Dup] is referenced by a measure; 'B'[Dup] (same name, different table) is not.
        var snapshot = new ModelSnapshot("M", 1601,
        [
            Table("A", Column("Dup", "A")),
            Table("B", Column("Dup", "B")),
            Table("Facts", Measure("Total", "Facts", "SUM ( 'A'[Dup] )"))
        ]);

        var model = BpaModelBuilder.Build(snapshot);

        Assert.True(Col(model, "A", "Dup").ReferencedBy.Count > 0);   // referenced
        Assert.Equal(0, Col(model, "B", "Dup").ReferencedBy.Count);   // NOT referenced (was the bug)

        // The referencing measure is attributed only to A's column.
        Assert.Contains(Col(model, "A", "Dup").ReferencedBy.AllMeasures, m => m.Name == "Total");
        Assert.Empty(Col(model, "B", "Dup").ReferencedBy.AllMeasures);
    }

    [Fact]
    public void UnqualifiedReference_CountsTowardEverySameNamedColumn()
    {
        // An unqualified [Dup] cannot be disambiguated, so it conservatively counts for both columns.
        var snapshot = new ModelSnapshot("M", 1601,
        [
            Table("A", Column("Dup", "A")),
            Table("B", Column("Dup", "B")),
            Table("Facts", Measure("Total", "Facts", "SUMX ( A, [Dup] )"))
        ]);

        var model = BpaModelBuilder.Build(snapshot);

        Assert.True(Col(model, "A", "Dup").ReferencedBy.Count > 0);
        Assert.True(Col(model, "B", "Dup").ReferencedBy.Count > 0);
    }

    [Fact]
    public void UnnecessaryColumns_FlagsOnlyTheUnreferencedSameNamedColumn()
    {
        // End-to-end through the bundled rule: hidden 'B'[Dup] is unnecessary; hidden 'A'[Dup] is
        // referenced (qualified) and must not be flagged.
        var snapshot = new ModelSnapshot("M", 1601,
        [
            Table("A", Column("Dup", "A")),
            Table("B", Column("Dup", "B")),
            Table("Facts", Measure("Total", "Facts", "SUM ( 'A'[Dup] )"))
        ]);

        var rule = BpaRuleLoader.LoadBundledCatalog().Single(r => r.Id == "UNNECESSARY_COLUMNS");
        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions([rule]));

        var paths = result.Violations.Select(v => v.ObjectPath).OrderBy(p => p).ToArray();
        Assert.Contains("B/Dup", paths);
        Assert.DoesNotContain("A/Dup", paths);
    }
}
