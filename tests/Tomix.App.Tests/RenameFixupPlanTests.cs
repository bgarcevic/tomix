using Tomix.App.Mutations;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// The fixup plan splice-rewrites references by exact span, so surrounding formatting and
/// comments survive and every reference form (qualified, unqualified, table-only, bare table)
/// comes out pointing at the new name.
/// </summary>
public sealed class RenameFixupPlanTests
{
    [Fact]
    public async Task MeasureRename_RewritesQualifiedAndUnqualifiedForms()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Measure("Base", "Sales/Base", "1"),
                Measure("A", "Sales/A", "[Base] + 'Sales'[Base] + Sales[Base]"),
            ],
            "Sales/Base", "New");

        var edit = Assert.Single(plan.Edits);
        Assert.Equal("[New] + 'Sales'[New] + 'Sales'[New]", edit.Value);
    }

    [Fact]
    public async Task MeasureRename_PreservesFormattingAndComments()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Measure("Base", "Sales/Base", "1"),
                Measure("A", "Sales/A", "// keep me\nVAR x = [Base]  -- and me\nRETURN x"),
            ],
            "Sales/Base", "New");

        Assert.Equal("// keep me\nVAR x = [New]  -- and me\nRETURN x", plan.Edits[0].Value);
    }

    [Fact]
    public async Task TableRename_RewritesEveryReferenceForm()
    {
        var plan = await Plan(
            [
                Table("Region"),
                Table("Sales"),
                Measure("A", "Sales/A", "COUNTROWS('Region') + COUNTROWS(Region) + SUM('Region'[Amount])"),
            ],
            "Region", "Geo");

        var edit = Assert.Single(plan.Edits);
        Assert.Equal("COUNTROWS('Geo') + COUNTROWS('Geo') + SUM('Geo'[Amount])", edit.Value);
    }

    [Fact]
    public async Task NewNames_AreEscapedInRewrittenReferences()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Measure("Base", "Sales/Base", "1"),
                Measure("A", "Sales/A", "[Base]"),
            ],
            "Sales/Base", "QA's ]Odd] Name");

        Assert.Equal("[QA's ]]Odd]] Name]", plan.Edits[0].Value);
    }

    [Fact]
    public async Task TableRename_WithApostrophe_EscapesQuotedForm()
    {
        var plan = await Plan(
            [
                Table("Region"),
                Table("Sales"),
                Measure("A", "Sales/A", "COUNTROWS('Region')"),
            ],
            "Region", "KPI'er");

        Assert.Equal("COUNTROWS('KPI''er')", plan.Edits[0].Value);
    }

    [Fact]
    public async Task MeasureRename_RoutesKpiPropertyKey()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Measure("Base", "Sales/Base", "1"),
                Measure("Kpi", "Sales/Kpi", "2",
                    new Dictionary<string, string> { ["KpiTargetExpression"] = "[Base] * 1.1" }),
            ],
            "Sales/Base", "New");

        var edit = Assert.Single(plan.Edits);
        Assert.Equal("KpiTargetExpression", edit.Property);
        Assert.Equal("[New] * 1.1", edit.Value);
    }

    [Fact]
    public async Task ColumnRename_RewritesTableDefaultDetailRows()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Column("Amount", "Sales/Amount"),
                Table("Digest", new Dictionary<string, string>
                {
                    ["DefaultDetailRowsExpression"] = "SELECTCOLUMNS(Sales, \"A\", Sales[Amount])"
                }),
            ],
            "Sales/Amount", "Net");

        var edit = Assert.Single(plan.Edits);
        Assert.Equal("Digest", edit.Path);
        Assert.Equal("DefaultDetailRowsExpression", edit.Property);
        Assert.Equal("SELECTCOLUMNS(Sales, \"A\", 'Sales'[Net])", edit.Value);
        Assert.Empty(plan.UnfixablePaths);
    }

    [Fact]
    public async Task CaseOnlyRename_PlansNothing()
    {
        var plan = await Plan(
            [
                Table("Sales"),
                Measure("Base", "Sales/Base", "1"),
                Measure("A", "Sales/A", "[Base]"),
            ],
            "Sales/Base", "BASE");

        Assert.Empty(plan.Edits);
        Assert.Empty(plan.AllPaths);
    }

    private static async Task<RenameFixupPlan> Plan(
        IReadOnlyList<ModelObject> objects, string path, string newName)
        => await RenameFixup.PlanAsync(
            new StubSession(new ModelSnapshot("M", 1601, objects)),
            path, type: null, newName, CancellationToken.None);

    private static ModelObject Table(string name, IReadOnlyDictionary<string, string>? properties = null)
        => new(name, ModelObjectKind.Table, name,
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [], Properties: properties);

    private static ModelObject Column(string name, string path)
        => new(name, ModelObjectKind.Column, path,
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []);

    private static ModelObject Measure(
        string name, string path, string expression, IReadOnlyDictionary<string, string>? properties = null)
        => new(name, ModelObjectKind.Measure, path,
            Detail: null, Expression: expression, Description: null, Hidden: false, SourceColumn: null,
            Children: [], Properties: properties);

    private sealed class StubSession : IModelSession
    {
        private readonly ModelSnapshot _snapshot;

        public StubSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(_snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
