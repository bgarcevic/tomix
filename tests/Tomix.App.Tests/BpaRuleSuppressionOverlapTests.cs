using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// A rule can be switched off at two levels: disabled for the current user
/// (<c>bpa rules disable</c>, <c>bpa-disabled.json</c>) and ignored by the model
/// (<c>bpa rules ignore</c>, the <c>BestPracticeAnalyzer_IgnoreRules</c> annotation). Turning one
/// level back on must say when the other still keeps the rule off, and list/run must show both.
/// </summary>
public sealed class BpaRuleSuppressionOverlapTests
{
    private const string StandardRule = "HIDE_FOREIGN_KEYS";
    private const string FullOnlyRule = "UNNECESSARY_COLUMNS";

    // --- unignore --user----------------------------------------------------------------------

    [Fact]
    public async Task Enable_RuleTheActiveModelStillIgnores_WarnsWithTheUnignoreCommand()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        await IgnoreAsync(config, model, StandardRule);
        var state = new BpaUserRuleState(config.Path);
        state.Disable(StandardRule);

        var result = await DisableHandler(config).HandleAsync(
            new BpaRulesDisableRequest(StandardRule, Disable: false, ActiveModel: new ModelReference(model)),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Data!.Changed);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "TOMIX_BPA_RULE_STILL_IGNORED_BY_MODEL");
        Assert.Contains($"tx bpa rules unignore {StandardRule}", warning.Hint);
    }

    [Fact]
    public async Task Enable_NoActiveModel_DoesNotWarnAboutModelIgnores()
    {
        using var config = new TempConfigDir();
        new BpaUserRuleState(config.Path).Disable(StandardRule);

        var result = await DisableHandler(config).HandleAsync(
            new BpaRulesDisableRequest(StandardRule, Disable: false), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Enable_UnreadableActiveModel_StillEnables()
    {
        // The model check is best effort: a stale active connection must not block enabling.
        using var config = new TempConfigDir();
        using var root = new TempDir();
        new BpaUserRuleState(config.Path).Disable(StandardRule);

        var result = await DisableHandler(config).HandleAsync(
            new BpaRulesDisableRequest(StandardRule, Disable: false, ActiveModel: new ModelReference(root.Combine("gone"))),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TOMIX_BPA_RULE_STILL_IGNORED_BY_MODEL");
    }

    [Fact]
    public async Task Enable_RuleOnlyInFull_WarnsThatTheDefaultRunSkipsIt()
    {
        // `enable` only undoes `disable`; it does not add a rule to the ruleset. Saying "already
        // enabled" for a full-only rule would suggest `bpa run` now checks it.
        using var config = new TempConfigDir();

        var result = await DisableHandler(config).HandleAsync(
            new BpaRulesDisableRequest(FullOnlyRule, Disable: false), CancellationToken.None);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "TOMIX_BPA_RULE_NOT_IN_RULESET");
        Assert.Contains("--ruleset full", warning.Message + warning.Hint);
    }

    [Theory]
    [InlineData(StandardRule, false)]
    [InlineData(FullOnlyRule, true)]
    public async Task Enable_RuleInStandardOrInTheUserRulesFile_HasNoRulesetWarning(string ruleId, bool inUserFile)
    {
        using var config = new TempConfigDir();
        if (inUserFile)
            File.WriteAllText(config.Combine("bpa-rules.json"),
                $"[{{\"ID\":\"{ruleId}\",\"Name\":\"mine\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"false\"}}]");

        var result = await DisableHandler(config).HandleAsync(
            new BpaRulesDisableRequest(ruleId, Disable: false), CancellationToken.None);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TOMIX_BPA_RULE_NOT_IN_RULESET");
    }

    // --- unignore (model)------------------------------------------------------------------

    [Fact]
    public async Task Unignore_RuleStillDisabledForTheUser_WarnsWithTheEnableCommand()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        await IgnoreAsync(config, model, StandardRule);
        new BpaUserRuleState(config.Path).Disable(StandardRule);

        var result = await IgnoreHandler(config).HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference(model), StandardRule, Ignore: false, Save: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Changed);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "TOMIX_BPA_RULE_STILL_IGNORED_BY_USER");
        Assert.Contains($"tx bpa rules unignore {StandardRule} --user", warning.Hint);
    }

    [Fact]
    public async Task Unignore_RuleNotDisabled_HasNoWarning()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        await IgnoreAsync(config, model, StandardRule);

        var result = await IgnoreHandler(config).HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference(model), StandardRule, Ignore: false, Save: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    // --- list -----------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task List_RuleBothDisabledAndIgnored_ShowsBothLevels(bool disabledOnly, bool ignoredOnly)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        await IgnoreAsync(config, model, StandardRule);
        var state = new BpaUserRuleState(config.Path);
        state.Disable(StandardRule);

        var result = await new BpaRulesListHandler([new TmdlModelProvider()], state, configDirectory: config.Path).HandleAsync(
            new BpaRulesListRequest(Model: new ModelReference(model), DisabledOnly: disabledOnly, IgnoredOnly: ignoredOnly),
            CancellationToken.None);

        Assert.True(result.Success);
        var rule = Assert.Single(result.Data!.Rules, r => r.Id == StandardRule);
        Assert.True(rule.Disabled);
        Assert.True(rule.Ignored);
        Assert.False(rule.Enabled);
        Assert.Equal(1, result.Data.Summary.Disabled);
        Assert.Equal(1, result.Data.Summary.Ignored);
        Assert.Equal(result.Data.Summary.Total - 1, result.Data.Summary.Active);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task List_Ignored_IncludesEitherLevel(bool byUser, bool byModel)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        if (byModel)
            await IgnoreAsync(config, model, StandardRule);
        var state = new BpaUserRuleState(config.Path);
        if (byUser)
            state.Disable(StandardRule);

        var result = await new BpaRulesListHandler([new TmdlModelProvider()], state, configDirectory: config.Path).HandleAsync(
            new BpaRulesListRequest(Model: new ModelReference(model), IgnoredOnly: true),
            CancellationToken.None);

        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal(StandardRule, rule.Id);
        Assert.Equal(byUser, rule.Disabled);
        Assert.Equal(byModel, rule.Ignored);
    }

    // --- run ------------------------------------------------------------------------------

    [Fact]
    public async Task Run_ReportsWhichLevelSwitchedEachRuleOff()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        await IgnoreAsync(config, model, "ONLY_IGNORED");
        await IgnoreAsync(config, model, "BOTH");
        var state = new BpaUserRuleState(config.Path);
        state.Disable("ONLY_DISABLED");
        state.Disable("BOTH");
        var rules = root.WriteFile("rules.json", "[" + string.Join(",",
            new[] { "ONLY_IGNORED", "ONLY_DISABLED", "BOTH" }.Select(id =>
                $"{{\"ID\":\"{id}\",\"Name\":\"{id}\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\"}}")) + "]");

        var result = await new BpaRunHandler([new TmdlModelProvider()], config.Stores, state, config.Path)
            .HandleAsync(new BpaRunRequest(new ModelReference(model), RulesFiles: [rules], NoDefaults: true), CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(3, result.Data!.DisabledRules);
        Assert.Equal(["BOTH", "ONLY_DISABLED"], result.Data.UserIgnoredRules.Order());
        Assert.Equal(["BOTH", "ONLY_IGNORED"], result.Data.ModelIgnoredRules.Order());
    }

    // --- helpers --------------------------------------------------------------------------

    private static BpaRulesDisableHandler DisableHandler(TempConfigDir config)
        => new(new BpaUserRuleState(config.Path), config.Path, [new TmdlModelProvider()]);

    private static BpaRulesIgnoreHandler IgnoreHandler(TempConfigDir config)
        => new([new TmdlModelProvider()], config.Stores, config.Path);

    private static async Task IgnoreAsync(TempConfigDir config, string model, string ruleId)
    {
        var result = await IgnoreHandler(config).HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference(model), ruleId, Ignore: true, Save: true, AllowUnknown: true),
            CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
