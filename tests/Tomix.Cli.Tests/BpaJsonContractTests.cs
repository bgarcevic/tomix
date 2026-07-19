using System.Text.Json;
using Tomix.App.Bpa;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the <c>tx bpa run</c> and <c>tx bpa rules list</c> JSON contracts (field names,
/// severity as int + label, conditional omission of empty rule fields, and the
/// non-violation diagnostics filter). Changes here are breaking for scripted
/// consumers — additive only.
/// </summary>
public sealed class BpaJsonContractTests
{
    private static BpaRule SampleRule(string id = "AVOID_FLOATS", BpaSeverity severity = BpaSeverity.Error)
        => new(id, $"[Performance] {id}", "Performance", severity, Scope: ["Column"], Expression: "true");

    private static BpaViolation SampleViolation(string objectName = "Sales[Amount]")
        => new(
            RuleId: "AVOID_FLOATS",
            RuleName: "[Performance] Avoid floats",
            Category: "Performance",
            Severity: BpaSeverity.Error,
            ObjectType: "Column",
            ObjectName: objectName,
            ObjectPath: $"model/tables/{objectName}",
            Description: "Do not use floating point.\nReference: https://example.test",
            CanFix: true);

    private static BpaRunResult SampleRunResult() => new(
        Results:
        [
            BpaResult.ForViolation(SampleRule(), SampleViolation()),
            BpaResult.ForViolation(SampleRule(), SampleViolation("Sales[Tax]"), isIgnored: true),
            BpaResult.Sentinel(BpaResultKind.CompilationError, SampleRule("BROKEN_RULE"), "boom", "model"),
            BpaResult.Sentinel(BpaResultKind.DisabledRule, SampleRule("OFF_RULE"))
        ],
        ModelName: "MyModel",
        RulesEvaluated: 4,
        DurationMs: 12,
        FixesApplied: 1,
        FixesSkipped: 2,
        DestructiveFixesSkipped: 3,
        FixErrors: ["fix failed"],
        Saved: true,
        Staged: null,
        RuleLoadDiagnostics: ["could not load extra.json"]);

    [Fact]
    public void RunJson_UsesDocumentedFieldNames()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRunRenderer.ToJson(SampleRunResult()))).RootElement;

        Assert.Equal(4, root.GetProperty("rulesEvaluated").GetInt32());
        Assert.Equal(1, root.GetProperty("violations").GetInt32());
        Assert.Equal(1, root.GetProperty("ruleErrors").GetInt32());
        Assert.Equal(1, root.GetProperty("ignoredRules").GetInt32());
        Assert.Equal(1, root.GetProperty("disabledRules").GetInt32());
        Assert.Equal(0, root.GetProperty("invalidCompatibilityRules").GetInt32());
        Assert.Equal(1, root.GetProperty("fixesApplied").GetInt32());
        Assert.Equal(2, root.GetProperty("fixesSkipped").GetInt32());
        Assert.Equal(3, root.GetProperty("destructiveFixesSkipped").GetInt32());
        Assert.Equal("fix failed", root.GetProperty("fixErrors")[0].GetString());
        Assert.Equal("could not load extra.json", root.GetProperty("ruleLoadDiagnostics")[0].GetString());
        Assert.True(root.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("staged").ValueKind);
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public void RunJson_ResultItems_UseSeverityIntAndLabel()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRunRenderer.ToJson(SampleRunResult()))).RootElement;
        var results = root.GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        var item = results[0];
        Assert.Equal("AVOID_FLOATS", item.GetProperty("ruleId").GetString());
        Assert.Equal("[Performance] Avoid floats", item.GetProperty("ruleName").GetString());
        Assert.Equal("Performance", item.GetProperty("category").GetString());
        Assert.Equal((int)BpaSeverity.Error, item.GetProperty("severity").GetInt32());
        Assert.Equal("Error", item.GetProperty("severityLabel").GetString());
        Assert.Equal("Sales[Amount]", item.GetProperty("objectName").GetString());
        Assert.Equal("Column", item.GetProperty("objectType").GetString());
        Assert.True(item.GetProperty("canFix").GetBoolean());
    }

    [Fact]
    public void RunJson_Diagnostics_ExcludeViolations()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRunRenderer.ToJson(SampleRunResult()))).RootElement;
        var diagnostics = root.GetProperty("diagnostics");

        Assert.Equal(2, diagnostics.GetArrayLength());
        Assert.Equal("CompilationError", diagnostics[0].GetProperty("kind").GetString());
        Assert.Equal("BROKEN_RULE", diagnostics[0].GetProperty("ruleId").GetString());
        Assert.Equal("model", diagnostics[0].GetProperty("scope").GetString());
        Assert.Equal("boom", diagnostics[0].GetProperty("message").GetString());
        Assert.Equal("DisabledRule", diagnostics[1].GetProperty("kind").GetString());
    }

    [Fact]
    public void RulesListJson_OmitsEmptyOptionalFields()
    {
        var result = new BpaRulesListResult(
            Rules:
            [
                new BpaRuleInfo(
                    Source: "built-in", Status: "active", Id: "R1", Name: "Rule one",
                    Category: "DAX", Severity: BpaSeverity.Warning, Scope: "Measure",
                    Description: "Guidance here.", Expression: "true", FixExpression: null, Enabled: true),
                new BpaRuleInfo(
                    Source: "model", Status: "ignored", Id: "R2", Name: "Rule two",
                    Category: "Naming", Severity: BpaSeverity.Info, Scope: "Column",
                    Description: "  ", Expression: null, FixExpression: "fix()", Enabled: false)
            ],
            Summary: new BpaRulesSummary(Total: 2, Active: 1, Disabled: 0, Ignored: 1));

        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRulesRenderer.ToListJson(result))).RootElement;
        var rules = root.GetProperty("rules");

        var first = rules[0];
        Assert.Equal("built-in", first.GetProperty("source").GetString());
        Assert.Equal("active", first.GetProperty("status").GetString());
        Assert.Equal("R1", first.GetProperty("id").GetString());
        Assert.Equal((int)BpaSeverity.Warning, first.GetProperty("severity").GetInt32());
        Assert.Equal("Warning", first.GetProperty("severityLabel").GetString());
        Assert.Equal("Measure", first.GetProperty("scope").GetString());
        Assert.Equal("Guidance here.", first.GetProperty("description").GetString());
        Assert.Equal("true", first.GetProperty("expression").GetString());
        Assert.False(first.TryGetProperty("fixExpression", out _));

        var second = rules[1];
        Assert.False(second.TryGetProperty("description", out _));
        Assert.False(second.TryGetProperty("expression", out _));
        Assert.Equal("fix()", second.GetProperty("fixExpression").GetString());

        Assert.Equal(2, root.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public void DisableJson_UsesDocumentedFieldNames()
    {
        var result = new BpaRulesDisableResult("R1", Disabled: true, Changed: true, DisabledRuleIds: ["R1"]);
        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRulesRenderer.ToDisableJson(result))).RootElement;

        Assert.Equal("R1", root.GetProperty("ruleId").GetString());
        Assert.True(root.GetProperty("disabled").GetBoolean());
        Assert.True(root.GetProperty("changed").GetBoolean());
        Assert.Equal(1, root.GetProperty("disabledRuleIds").GetArrayLength());
    }

    [Fact]
    public void IgnoreJson_UsesDocumentedFieldNames()
    {
        var result = new BpaRulesIgnoreResult(
            RuleId: "R1", Ignored: true, Changed: true, RuleIds: ["R1"],
            Saved: true, Staged: null, ModelName: "MyModel");
        var root = JsonDocument.Parse(JsonOutput.Serialize(BpaRulesRenderer.ToIgnoreJson(result))).RootElement;

        Assert.Equal("R1", root.GetProperty("ruleId").GetString());
        Assert.True(root.GetProperty("ignored").GetBoolean());
        Assert.True(root.GetProperty("changed").GetBoolean());
        Assert.Equal(1, root.GetProperty("ruleIds").GetArrayLength());
        Assert.True(root.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("staged").ValueKind);
        Assert.Equal("MyModel", root.GetProperty("model").GetString());
    }
}
