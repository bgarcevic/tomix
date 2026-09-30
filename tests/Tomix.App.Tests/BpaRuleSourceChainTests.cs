using Tomix.App.Bpa;
using Tomix.Core.Configuration;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// #233: rule files come from a chain — the config-dir <c>bpa-rules.json</c>, then the
/// <c>bpa.rules</c> config key, then <c>TOMIX_BPA_RULES</c>, then <c>--rules</c> — where a later
/// link overrides an earlier one for the same rule id, and the run names every source it used.
/// </summary>
public sealed class BpaRuleSourceChainTests
{
    // Every link defines the same id with a different name, so the reported name identifies the
    // link that won. The expression matches every table, so the rule always yields a finding.
    private static string ChainRule(string marker)
        => $"[{{\"ID\":\"CHAIN\",\"Name\":\"{marker}\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}}]";

    [Theory]
    [InlineData(true, true, true, true, "option")]
    [InlineData(true, true, true, false, "env")]
    [InlineData(true, true, false, false, "config")]
    [InlineData(true, false, false, false, "user-file")]
    [InlineData(false, true, false, true, "option")]
    public async Task Run_LaterLinkInTheChainWins(bool userFile, bool config, bool env, bool option, string expected)
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");

        if (userFile)
            File.WriteAllText(configDir.Combine("bpa-rules.json"), ChainRule("user-file"));
        if (config)
            WriteConfig(configDir, root.WriteFile("config-rules.json", ChainRule("config")));
        var envValue = env ? root.WriteFile("env-rules.json", ChainRule("env")) : null;
        string[]? optionFiles = option ? [root.WriteFile("option-rules.json", ChainRule("option"))] : null;

        var result = await RunAsync(configDir, model, envValue, r => r with { NoDefaults = true, RulesFiles = optionFiles });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.All(result.Data!.Violations, v => Assert.Equal(expected, v.RuleName));
        Assert.NotEmpty(result.Data.Violations);
    }

    [Fact]
    public async Task Run_ConfigAndEnvironmentAcceptSeveralSemicolonSeparatedFiles()
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var a = root.WriteFile("a.json", ChainRule("a").Replace("CHAIN", "RULE_A"));
        var b = root.WriteFile("b.json", ChainRule("b").Replace("CHAIN", "RULE_B"));

        var result = await RunAsync(configDir, model, $" {a} ; {b} ;", r => r with { NoDefaults = true });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(["RULE_A", "RULE_B"], result.Data!.Violations.Select(v => v.RuleId).Distinct().Order());
    }

    [Fact]
    public async Task Run_RelativeConfigPath_ResolvesAgainstTheConfigDirectory()
    {
        // A relative bpa.rules entry is anchored at the config directory, not the working
        // directory, so the same config behaves the same from any folder.
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        configDir.CreateSubdirectory("team");
        File.WriteAllText(configDir.Combine("team", "rules.json"), ChainRule("config"));
        WriteConfig(configDir, Path.Combine("team", "rules.json"));

        var result = await RunAsync(configDir, model, env: null, r => r with { NoDefaults = true });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Data!.Violations, v => v.RuleName == "config");
    }

    [Fact]
    public async Task Run_MissingEnvironmentFile_FailsNamingTheVariable()
    {
        // A CI variable that points at a missing file must fail loudly, not run without the
        // team's rules — and the message says which link of the chain was wrong.
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");

        var result = await RunAsync(configDir, model, root.Combine("missing.json"), r => r with { NoDefaults = true });

        Assert.False(result.Success);
        Assert.Equal("TOMIX_BPA_RULES_LOAD_FAILED", result.Diagnostics[0].Code);
        Assert.Contains(BpaRuleSources.EnvironmentVariable, result.Diagnostics[0].Message);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Run_ReportsEverySourceItLoaded_InChainOrder()
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var configRules = root.WriteFile("config-rules.json", ChainRule("config").Replace("CHAIN", "FROM_CONFIG"));
        WriteConfig(configDir, configRules);
        var envRules = root.WriteFile("env-rules.json", ChainRule("env").Replace("CHAIN", "FROM_ENV"));

        var result = await RunAsync(configDir, model, envRules, r => r with { NoModelRules = true });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var sources = result.Data!.RuleSources;
        Assert.Equal(
            [BpaRuleOrigin.Ruleset, BpaRuleOrigin.Config, BpaRuleOrigin.Environment],
            sources.Select(s => s.Origin));
        Assert.Equal("standard", sources[0].Name);
        Assert.Equal(configRules, sources[1].Name);
        Assert.Equal(envRules, sources[2].Name);
        Assert.Equal(result.Data.RulesEvaluated, sources.Sum(s => s.Rules));
    }

    [Theory]
    [InlineData(true, "bpa.rules")]
    [InlineData(false, "TOMIX_BPA_RULES")]
    public async Task List_IncludesTheConfiguredSources_UnderTheSettingThatNamesThem(bool viaConfig, string source)
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var rules = root.WriteFile("team.json", ChainRule("team"));
        if (viaConfig)
            WriteConfig(configDir, rules);
        var env = viaConfig ? null : rules;

        var result = await new BpaRulesListHandler(
                null, new BpaUserRuleState(configDir.Path), configDirectory: configDir.Path,
                environment: name => name == BpaRuleSources.EnvironmentVariable ? env : null)
            .HandleAsync(new BpaRulesListRequest(NoDefaults: true), CancellationToken.None);

        Assert.True(result.Success);
        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal("CHAIN", rule.Id);
        Assert.Equal(source, rule.Source);
    }

    [Fact]
    public async Task List_CombinedPresets_ShowEachRuleOnce()
    {
        // `--ruleset standard,full`: every standard rule is also in full, so listing both copies
        // doubled the count.
        using var configDir = new TempConfigDir();
        var state = new BpaUserRuleState(configDir.Path);
        var full = await new BpaRulesListHandler(null, state)
            .HandleAsync(new BpaRulesListRequest(Ruleset: "full"), CancellationToken.None);
        var combined = await new BpaRulesListHandler(null, state)
            .HandleAsync(new BpaRulesListRequest(Ruleset: "standard,full"), CancellationToken.None);

        Assert.Equal(full.Data!.Summary.Total, combined.Data!.Summary.Total);
        Assert.Equal(combined.Data.Rules.Count, combined.Data.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(combined.Data.Rules, r => Assert.Equal("full", r.Source));
    }

    [Fact]
    public async Task List_TeamRuleOverridingABuiltIn_ShowsOnceFromTheTeamFile_ShowKeepsBoth()
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var env = root.WriteFile("team.json",
            "[{\"ID\":\"HIDE_FOREIGN_KEYS\",\"Name\":\"Team keys\",\"Category\":\"c\",\"Severity\":3,\"Scope\":\"DataColumn\",\"Expression\":\"true\"}]");
        var handler = new BpaRulesListHandler(
            null, new BpaUserRuleState(configDir.Path), configDirectory: configDir.Path,
            environment: name => name == BpaRuleSources.EnvironmentVariable ? env : null);

        var list = await handler.HandleAsync(new BpaRulesListRequest(), CancellationToken.None);
        var show = await handler.HandleAsync(new BpaRulesListRequest(RuleId: "HIDE_FOREIGN_KEYS"), CancellationToken.None);

        var rule = Assert.Single(list.Data!.Rules, r => r.Id == "HIDE_FOREIGN_KEYS");
        Assert.Equal(BpaRuleSources.EnvironmentVariable, rule.Source);
        Assert.Equal("Team keys", rule.Name);
        Assert.Equal(["standard"], rule.Overrides!);
        Assert.Equal(["standard", BpaRuleSources.EnvironmentVariable], show.Data!.Rules.Select(r => r.Source));
    }

    [Fact]
    public async Task List_UnloadableConfiguredSource_IsReportedNotFatal()
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var missing = root.Combine("missing.json");

        var result = await new BpaRulesListHandler(
                null, new BpaUserRuleState(configDir.Path), configDirectory: configDir.Path,
                environment: name => name == BpaRuleSources.EnvironmentVariable ? missing : null)
            .HandleAsync(new BpaRulesListRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Data!.Rules);
        Assert.Contains(result.Data.Diagnostics!, d => d.Contains(BpaRuleSources.EnvironmentVariable));
    }

    [Fact]
    public void KnownRules_IncludeIdsFromTheConfiguredSources()
    {
        using var configDir = new TempConfigDir();
        using var root = new TempDir();
        var env = root.WriteFile("team.json", ChainRule("team"));

        var known = BpaKnownRules.Load(
            configDir.Path, environment: name => name == BpaRuleSources.EnvironmentVariable ? env : null);

        Assert.Null(known.Check<object>("CHAIN"));
    }

    private static void WriteConfig(TempConfigDir configDir, string rules)
        => new Config.TomixConfigStore(Platform.Configuration.TomixPaths.ConfigFileIn(configDir.Path))
            .Save(new Dictionary<string, string> { [ConfigKeys.BpaRules] = rules });

    private static Task<TomixResult<BpaRunResult>> RunAsync(
        TempConfigDir config, string model, string? env, Func<BpaRunRequest, BpaRunRequest> configure)
        => new BpaRunHandler(
                [new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path,
                environment: name => name == BpaRuleSources.EnvironmentVariable ? env : null)
            .HandleAsync(configure(new BpaRunRequest(new ModelReference(model))), CancellationToken.None);
}
