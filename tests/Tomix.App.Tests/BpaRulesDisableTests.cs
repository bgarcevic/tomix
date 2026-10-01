using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.App.Tests;

public sealed class BpaRulesDisableTests
{
    [Fact]
    public void Disable_PersistsAndIsIdempotent()
    {
        using var dir = new TempDir();
        var state = new BpaUserRuleState(dir.Path);

        Assert.True(state.Disable("RULE_A"));
        Assert.False(state.Disable("RULE_A"));            // already disabled
        Assert.Contains("RULE_A", state.GetDisabled());

        // A fresh instance reads the persisted file.
        Assert.Contains("RULE_A", new BpaUserRuleState(dir.Path).GetDisabled());
    }

    [Fact]
    public void Disable_IsCaseInsensitive_AndEnableReverts()
    {
        using var dir = new TempDir();
        var state = new BpaUserRuleState(dir.Path);
        state.Disable("rule_a");

        Assert.Contains("RULE_A", state.GetDisabled());   // case-insensitive membership
        Assert.True(state.Enable("RULE_A"));
        Assert.Empty(state.GetDisabled());
        Assert.False(state.Enable("RULE_A"));             // already enabled
    }

    [Fact]
    public void Handler_DisableThenEnable_ReportsChange()
    {
        using var dir = new TempDir();
        var handler = new BpaRulesDisableHandler(new BpaUserRuleState(dir.Path));

        var disabled = handler.Handle(new BpaRulesDisableRequest("HIDE_FOREIGN_KEYS", Disable: true));
        Assert.True(disabled.Success);
        Assert.True(disabled.Data!.Changed);
        Assert.Contains("HIDE_FOREIGN_KEYS", disabled.Data.DisabledRuleIds);

        var enabled = handler.Handle(new BpaRulesDisableRequest("HIDE_FOREIGN_KEYS", Disable: false));
        Assert.True(enabled.Data!.Changed);
        Assert.Empty(enabled.Data.DisabledRuleIds);
    }

    [Fact]
    public void Handler_DisableUnknownRule_FailsAndChangesNothing()
    {
        using var dir = new TempDir();
        var state = new BpaUserRuleState(dir.Path);
        var handler = new BpaRulesDisableHandler(state, dir.Path);

        var result = handler.Handle(new BpaRulesDisableRequest("FLOATING", Disable: true));

        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_BPA_RULE_NOT_FOUND", error.Code);
        Assert.StartsWith("Did you mean: AVOID_FLOATING_POINT_DATA_TYPES?", error.Hint);
        Assert.Empty(state.GetDisabled());
    }

    [Fact]
    public void Handler_DisableRuleFromUserRulesFile_IsKnown()
    {
        using var dir = new TempDir();
        dir.WriteFile("bpa-rules.json",
            "[{\"ID\":\"MY_RULE\",\"Name\":\"m\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\"}]");
        var handler = new BpaRulesDisableHandler(new BpaUserRuleState(dir.Path), dir.Path);

        Assert.True(handler.Handle(new BpaRulesDisableRequest("MY_RULE", Disable: true)).Success);
    }

    [Fact]
    public void Handler_DisableRuleFromSelectedRulesFile_IsKnown()
    {
        using var dir = new TempDir();
        var rulesFile = dir.WriteFile("selected.json",
            "[{\"ID\":\"SELECTED_RULE\",\"Name\":\"m\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\"}]");
        var handler = new BpaRulesDisableHandler(new BpaUserRuleState(dir.Path), dir.Path);

        var result = handler.Handle(new BpaRulesDisableRequest(
            "SELECTED_RULE", Disable: true, RulesFile: rulesFile));

        Assert.True(result.Success);
        Assert.Contains("SELECTED_RULE", result.Data!.DisabledRuleIds);
    }

    [Fact]
    public void Handler_AllowUnknownAndEnable_SkipTheCheck()
    {
        using var dir = new TempDir();
        var handler = new BpaRulesDisableHandler(new BpaUserRuleState(dir.Path), dir.Path);

        Assert.True(handler.Handle(new BpaRulesDisableRequest("NOT_A_RULE", Disable: true, AllowUnknown: true)).Success);
        // Enabling must work for an ID that no longer exists anywhere.
        var enabled = handler.Handle(new BpaRulesDisableRequest("NOT_A_RULE", Disable: false));
        Assert.True(enabled.Success);
        Assert.True(enabled.Data!.Changed);
    }

    [Fact]
    public void Engine_UserDisabledRule_EmitsDisabledRuleSentinel()
    {
        var column = new ModelObject("Col", ModelObjectKind.Column, "T/Col",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: "Col",
            Children: [], Properties: new Dictionary<string, string> { ["ObjectType"] = "DataColumn" });
        var table = new ModelObject("T", ModelObjectKind.Table, "T",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [column], Properties: new Dictionary<string, string> { ["ObjectType"] = "Table" });
        var snapshot = new ModelSnapshot("M", 1601, [table]);

        var rules = new List<BpaRule>
        {
            new("RULE_A", "Rule A", "Test", RuleSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(
            snapshot, new BpaEngineOptions(rules, DisabledRuleIds: ["rule_a"]));

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.DisabledRules);
    }
}
