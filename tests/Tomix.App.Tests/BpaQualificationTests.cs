using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Regression tests for the DAX qualification rules — the bug that prompted the rewrite, where
/// <c>DAX_MEASURES_UNQUALIFIED</c> flagged every measure because a regex could not distinguish a
/// qualified column reference from a qualified measure reference.
/// </summary>
public sealed class BpaQualificationTests
{
    private const string MeasuresUnqualified =
        "DependsOn.Any(Key.ObjectType = \"Measure\" and Value.Any(FullyQualified))";

    private const string ColumnsFullyQualified =
        "DependsOn.Any(Key.ObjectType = \"Column\" and Value.Any(not FullyQualified))";

    private static ModelSnapshot SalesModel()
    {
        var amount = Column("Amount", "Sales");
        var total = Measure("Total", "Sales", "SUM('Sales'[Amount])");                 // qualified column ref
        var qualMeasureRef = Measure("QualMeasureRef", "Sales", "'Sales'[Total] + 1");  // qualified measure ref
        var unqualMeasureRef = Measure("UnqualMeasureRef", "Sales", "[Total] + 1");     // unqualified measure ref
        var unqualColRef = Measure("UnqualColRef", "Sales", "[Amount] + 1");            // unqualified column ref

        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [amount, total, qualMeasureRef, unqualMeasureRef, unqualColRef],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });

        return new ModelSnapshot("Sales", 1601, [sales]);
    }

    [Fact]
    public void MeasuresUnqualified_FlagsOnlyQualifiedMeasureReferences()
    {
        var result = Run(MeasuresUnqualified, "Measure");

        // 'Sales'[Total] is the only qualified *measure* reference. A qualified *column*
        // reference (SUM('Sales'[Amount])) must NOT be flagged — that was the bug.
        Assert.Equal(["QualMeasureRef"], FlaggedNames(result));
    }

    [Fact]
    public void ColumnsFullyQualified_FlagsOnlyUnqualifiedColumnReferences()
    {
        var result = Run(ColumnsFullyQualified, "Measure");

        // [Amount] is the only unqualified column reference.
        Assert.Equal(["UnqualColRef"], FlaggedNames(result));
    }

    [Fact]
    public void AllBundledRules_EvaluateWithoutErrorAndReportTrueCount()
    {
        var rules = BpaRuleLoader.LoadDefaultRules();
        Assert.True(rules.Count >= 60, $"expected the full bundled ruleset, got {rules.Count}");

        var result = new BpaEngine().Evaluate(SalesModel(), new BpaEngineOptions(rules));

        // Every rule is evaluated generically from its Expression (no 32->36 fudge).
        Assert.Equal(rules.Count, result.RulesEvaluated);

        // The real bundled measures-unqualified rule still pinpoints the qualified measure ref.
        Assert.Contains(result.Violations,
            v => v.RuleId == "DAX_MEASURES_UNQUALIFIED" && v.ObjectPath.EndsWith("/QualMeasureRef"));
    }

    private static BpaRunResult Run(string expression, params string[] scope)
    {
        var rules = new List<BpaRule>
        {
            new("RULE", "Rule", "DAX Expressions", BpaSeverity.Error, scope, Expression: expression)
        };
        return new BpaEngine().Evaluate(SalesModel(), new BpaEngineOptions(rules));
    }

    private static string[] FlaggedNames(BpaRunResult result)
        => result.Violations
            .Select(v => v.ObjectPath.Split('/').Last())
            .OrderBy(n => n)
            .ToArray();

    private static ModelObject Column(string name, string table)
        => new(name, ModelObjectKind.Column, $"{table}/{name}",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: name,
            Children: [],
            Properties: new Dictionary<string, string> { ["DataType"] = "Decimal", ["ObjectType"] = "DataColumn" });

    private static ModelObject Measure(string name, string table, string expression)
        => new(name, ModelObjectKind.Measure, $"{table}/{name}",
            Detail: null, Expression: expression, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Measure" });
}
