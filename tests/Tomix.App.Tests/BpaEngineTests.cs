using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaEngineTests
{
    [Fact]
    public void Evaluate_NoViolationsOnEmptyModel()
    {
        var snapshot = new ModelSnapshot("TestModel", 1601, []);
        var engine = new BpaEngine();
        var rules = new List<BpaRule>
        {
            new("HIDE_FK", "Hide FK", "Formatting", BpaSeverity.Warning, ["Column"])
        };

        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Empty(result.Violations);
        Assert.Equal("TestModel", result.ModelName);
        Assert.Equal(1, result.RulesEvaluated);
    }

    [Fact]
    public void Evaluate_DetectsFloatingPoint()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Amount", ModelObjectKind.Column, "Table/Amount",
                Detail: null, Expression: null, Description: null, Hidden: false,
                SourceColumn: "Amount",
                Children: [],
                Properties: new Dictionary<string, string> { ["DataType"] = "Double", ["ObjectType"] = "DataColumn" }));

        var rules = new List<BpaRule>
        {
            new("AVOID_FLOATING_POINT_DATA_TYPES", "No float", "Performance", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                Expression: "DataType = \"Double\"",
                FixExpression: "DataType = DataType.Decimal")
        };

        var engine = new BpaEngine();
        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("AVOID_FLOATING_POINT_DATA_TYPES", violation.RuleId);
        Assert.True(violation.CanFix);
        Assert.Equal(ModelObjectKind.Column, violation.ObjectKind);
    }

    [Fact]
    public void Evaluate_ViolationCanFix_TrueWhenFixExpression()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false,
                SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" }));

        var rules = new List<BpaRule>
        {
            new("HAS_FIX", "With fix", "Test", BpaSeverity.Info, ["DataColumn"],
                Expression: "not IsHidden",
                FixExpression: "IsHidden = true"),
            new("NO_FIX", "No fix", "Test", BpaSeverity.Info, ["DataColumn"],
                Expression: "not IsHidden")
        };

        var engine = new BpaEngine();
        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.All(result.Violations, v =>
        {
            if (v.RuleId == "HAS_FIX") Assert.True(v.CanFix);
            else Assert.False(v.CanFix);
        });
    }

    [Fact]
    public void Evaluate_PathFilter_FiltersByPath()
    {
        var col1 = new ModelObject("Col1", ModelObjectKind.Column, "Orders/Col1",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: "Col1",
            Children: [],
            Properties: new Dictionary<string, string> { ["DataType"] = "Double", ["ObjectType"] = "DataColumn" });

        var col2 = new ModelObject("Col2", ModelObjectKind.Column, "Products/Col2",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: "Col2",
            Children: [],
            Properties: new Dictionary<string, string> { ["DataType"] = "Double", ["ObjectType"] = "DataColumn" });

        var table1 = new ModelObject("Orders", ModelObjectKind.Table, "Orders",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [col1],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });

        var table2 = new ModelObject("Products", ModelObjectKind.Table, "Products",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [col2],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });

        var snapshot = new ModelSnapshot("Test", 1601, [table1, table2]);

        var rules = new List<BpaRule>
        {
            new("AVOID_FLOATING_POINT_DATA_TYPES", "No float", "Performance", BpaSeverity.Warning,
                ["DataColumn", "CalculatedColumn", "CalculatedTableColumn"],
                Expression: "DataType = \"Double\"")
        };

        var engine = new BpaEngine();
        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules, PathFilter: "Orders"));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("Orders/Col1", violation.ObjectPath);
    }

    [Fact]
    public void Evaluate_RuleIds_FiltersById()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false,
                SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["DataType"] = "Double", ["ObjectType"] = "DataColumn" }));

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden"),
            new("RULE_B", "Rule B", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var engine = new BpaEngine();
        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules, RuleIds: ["RULE_A"]));

        Assert.NotEmpty(result.Violations);
        Assert.All(result.Violations, v => Assert.Equal("RULE_A", v.RuleId));
    }

    [Fact]
    public void Evaluate_ScopeIsMatchedByObjectType_CalculatedColumnRuleSkipsDataColumns()
    {
        var dataCol = new ModelObject("DataCol", ModelObjectKind.Column, "T/DataCol",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "DataCol",
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var calcCol = new ModelObject("CalcCol", ModelObjectKind.Column, "T/CalcCol",
            Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "CalculatedColumn" });
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [dataCol, calcCol],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        // Scoped to CalculatedColumn only — the always-true predicate must skip the DataColumn.
        var rules = new List<BpaRule>
        {
            new("CALC_ONLY", "Calc only", "Test", BpaSeverity.Info, ["CalculatedColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("T/CalcCol", violation.ObjectPath);
    }

    [Fact]
    public void Evaluate_TableScope_ExcludesCalculatedAndCalculationGroupTables()
    {
        var normal = new ModelObject("Normal", ModelObjectKind.Table, "Normal",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var calc = new ModelObject("Calc", ModelObjectKind.Table, "Calc",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table", ["TableIsCalc"] = "true" });
        var calcGroup = new ModelObject("CalcGroup", ModelObjectKind.Table, "CalcGroup",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table", ["TableObjectType"] = "CalculationGroup" });
        var snapshot = new ModelSnapshot("M", 1601, [normal, calc, calcGroup]);

        var rules = new List<BpaRule>
        {
            new("TABLE_RULE", "Table rule", "Test", BpaSeverity.Info, ["Table"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("Normal", violation.ObjectPath);
    }

    [Fact]
    public void Evaluate_CompatibilityLevelTooLow_EmitsSentinelAndSkipsEvaluation()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" }),
            compatibilityLevel: 1500);

        var rules = new List<BpaRule>
        {
            // Expression would match every column, but the rule must not run on this old model.
            new("NEEDS_1600", "Needs 1600", "Test", BpaSeverity.Warning, ["DataColumn"],
                Expression: "not IsHidden", CompatibilityLevel: 1600)
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.InvalidCompatibilityRules);
        var sentinel = Assert.Single(result.Results);
        Assert.Equal(BpaResultKind.InvalidCompatibilityLevel, sentinel.Kind);
        Assert.Equal("NEEDS_1600", sentinel.RuleId);
    }

    [Fact]
    public void Evaluate_CompilationError_EmitsSentinelWithScope()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" }));

        var rules = new List<BpaRule>
        {
            new("BAD_EXPR", "Bad expression", "Test", BpaSeverity.Warning, ["DataColumn"],
                Expression: "ThisMemberDoesNotExist = 1")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.RuleErrors);
        var sentinel = Assert.Single(result.Results);
        Assert.Equal(BpaResultKind.CompilationError, sentinel.Kind);
        Assert.Equal("Column", sentinel.ErrorScope);
    }

    [Fact]
    public void Evaluate_EvaluationError_PreservesCleanMatchesAndReportsError()
    {
        var good = new ModelObject("Good", ModelObjectKind.Column, "T/Good",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "5",
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var bad = new ModelObject("Bad", ModelObjectKind.Column, "T/Bad",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "not-a-number",
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [good, bad],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        var rules = new List<BpaRule>
        {
            // Compiles fine; throws at runtime only for the non-numeric SourceColumn.
            new("NUMERIC_SOURCE", "Numeric source", "Test", BpaSeverity.Info, ["DataColumn"],
                Expression: "Convert.ToInt64(SourceColumn) > 0")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("T/Good", violation.ObjectPath);
        Assert.Equal(1, result.RuleErrors);
        Assert.Contains(result.Results, r => r.Kind == BpaResultKind.EvaluationError && r.ErrorScope == "Column");
    }

    [Fact]
    public void Evaluate_MissingAnnotationInConvert_EvaluatesFalseWithoutError()
    {
        // Vertipaq-style rules parse annotations that only exist after a statistics run; on a
        // model without them GetAnnotation must return null (Convert.ToInt64(null) is 0) so the
        // rule evaluates to false instead of erroring on every object.
        var big = new ModelObject("Big", ModelObjectKind.Column, "T/Big",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Big",
            Children: [],
            Properties: new Dictionary<string, string>
            {
                ["ObjectType"] = "DataColumn",
                ["Annotation:Vertipaq_Cardinality"] = "200000"
            });
        var plain = new ModelObject("Plain", ModelObjectKind.Column, "T/Plain",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Plain",
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [big, plain],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        var rules = new List<BpaRule>
        {
            new("HIGH_CARDINALITY", "High cardinality", "Performance", BpaSeverity.Warning, ["Column"],
                Expression: "Convert.ToInt64(GetAnnotation(\"Vertipaq_Cardinality\")) > 100000")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("T/Big", violation.ObjectPath);
        Assert.Equal(0, result.RuleErrors);
    }

    [Fact]
    public void Evaluate_FormatStringRule_HonorsFormatStringExpression()
    {
        // A measure with a dynamic format string satisfies the format-string rule even though
        // its static FormatString is empty (upstream rules check both properties).
        var dynamicFormat = new ModelObject("Dynamic", ModelObjectKind.Measure, "T/Dynamic",
            Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string> { ["FormatStringExpression"] = "\"0.0%\"" });
        var unformatted = new ModelObject("Plain", ModelObjectKind.Measure, "T/Plain",
            Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null,
            Children: [],
            Properties: new Dictionary<string, string>());
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [dynamicFormat, unformatted],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        var rules = new List<BpaRule>
        {
            new("PROVIDE_FORMAT_STRING_FOR_MEASURES", "Format measures", "Formatting", BpaSeverity.Error, ["Measure"],
                Expression: "not IsHidden \r\nand not Table.IsHidden \r\nand string.IsNullOrWhitespace(FormatString) \r\nand string.IsNullOrWhitespace(FormatStringExpression)")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("T/Plain", violation.ObjectPath);
        Assert.Equal(0, result.RuleErrors);
    }

    [Fact]
    public void Evaluate_GlobalDisable_EmitsDisabledRuleAndSkipsEvaluation()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" }),
            // Mixed-case id in the ignore list must match the rule's id case-insensitively.
            modelAnnotations: new Dictionary<string, string>
            {
                [$"Annotation:{BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"rule_a\"]}"
            });

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.DisabledRules);
        var sentinel = Assert.Single(result.Results);
        Assert.Equal(BpaResultKind.DisabledRule, sentinel.Kind);
        Assert.Equal("RULE_A", sentinel.RuleId);
    }

    [Fact]
    public void Evaluate_LegacyMisspelledKey_IsHonoredForGlobalDisable()
    {
        var snapshot = CreateSnapshotWithColumn(
            new ModelObject("Col", ModelObjectKind.Column, "T/Col",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
                Children: [],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" }),
            modelAnnotations: new Dictionary<string, string>
            {
                [$"Annotation:{BpaIgnoreStore.LegacyKey}"] = "{\"RuleIDs\":[\"RULE_A\"]}"
            });

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Equal(1, result.DisabledRules);
    }

    [Fact]
    public void Evaluate_ObjectLevelIgnore_SuppressesVisibleButKeepsRaw()
    {
        var col = new ModelObject("Col", ModelObjectKind.Column, "T/Col",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
            Children: [],
            Properties: new Dictionary<string, string>
            {
                ["ObjectType"] = "DataColumn",
                [$"Annotation:{BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"RULE_A\"]}"
            });
        var snapshot = CreateSnapshotWithColumn(col);

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        Assert.Empty(result.Violations);              // excluded from the visible stream
        Assert.Equal(1, result.IgnoredViolations);    // retained in the raw stream as ignored
        Assert.Equal(0, result.DisabledRules);        // an object-level ignore does not disable the rule
        var raw = Assert.Single(result.Results);
        Assert.Equal(BpaResultKind.Violation, raw.Kind);
        Assert.True(raw.IsIgnored);
    }

    [Fact]
    public void Evaluate_IgnoreOnTable_DoesNotSuppressColumnViolation()
    {
        // The ignore lives on the table; the rule violates a column. No parent inheritance (spec test E).
        var col = new ModelObject("Col", ModelObjectKind.Column, "T/Col",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
            Children: [],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [col],
            Properties: new Dictionary<string, string>
            {
                ["ObjectType"] = "Table",
                [$"Annotation:{BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"RULE_A\"]}"
            });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var violation = Assert.Single(result.Violations);
        Assert.Equal("T/Col", violation.ObjectPath);
        Assert.Equal(0, result.IgnoredViolations);
    }

    private static ModelSnapshot CreateSnapshotWithColumn(
        ModelObject column,
        int compatibilityLevel = 1601,
        IReadOnlyDictionary<string, string>? modelAnnotations = null)
    {
        var table = new ModelObject("Table", ModelObjectKind.Table, "Table",
            Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [column],
            Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });

        return new ModelSnapshot("TestModel", compatibilityLevel, [table], modelAnnotations);
    }
}
