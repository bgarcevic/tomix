using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaRulesDisableTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tomix-bpa-disable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Disable_PersistsAndIsIdempotent()
    {
        var dir = TempDir();
        try
        {
            var state = new BpaUserRuleState(dir);

            Assert.True(state.Disable("RULE_A"));
            Assert.False(state.Disable("RULE_A"));            // already disabled
            Assert.Contains("RULE_A", state.GetDisabled());

            // A fresh instance reads the persisted file.
            Assert.Contains("RULE_A", new BpaUserRuleState(dir).GetDisabled());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Disable_IsCaseInsensitive_AndEnableReverts()
    {
        var dir = TempDir();
        try
        {
            var state = new BpaUserRuleState(dir);
            state.Disable("rule_a");

            Assert.Contains("RULE_A", state.GetDisabled());   // case-insensitive membership
            Assert.True(state.Enable("RULE_A"));
            Assert.Empty(state.GetDisabled());
            Assert.False(state.Enable("RULE_A"));             // already enabled
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Handler_DisableThenEnable_ReportsChange()
    {
        var dir = TempDir();
        try
        {
            var handler = new BpaRulesDisableHandler(new BpaUserRuleState(dir));

            var disabled = handler.Handle(new BpaRulesDisableRequest("RULE_A", Disable: true));
            Assert.True(disabled.Success);
            Assert.True(disabled.Data!.Changed);
            Assert.Contains("RULE_A", disabled.Data.DisabledRuleIds);

            var enabled = handler.Handle(new BpaRulesDisableRequest("RULE_A", Disable: false));
            Assert.True(enabled.Data!.Changed);
            Assert.Empty(enabled.Data.DisabledRuleIds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
            new("RULE_A", "Rule A", "Test", BpaSeverity.Warning, ["DataColumn"], Expression: "not IsHidden")
        };

        var result = new BpaEngine().Evaluate(
            snapshot, new BpaEngineOptions(rules, DisabledRuleIds: ["rule_a"]));

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.DisabledRules);
    }
}
