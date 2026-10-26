using Tomix.App.Bpa;
using Tomix.App.Save;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Platform.Configuration;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// #236: a project commits its BPA policy in <c>tomix.json</c>. The more local a setting, the
/// more it wins: the command line, then the <c>tomix.json</c> nearest the model, then ones above
/// it up to the repository root, then the file <c>TOMIX_PROJECT_CONFIG</c> names.
/// </summary>
public sealed class BpaProjectConfigTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    // A repository whose model sits two folders below the root, so a test can place tomix.json
    // at the root, in between, and next to the model. The .git folder keeps the search from
    // leaving the temp directory.
    private static (TempDir Root, string Model) Repository()
    {
        var root = new TempDir();
        root.CreateSubdirectory(".git");
        return (root, root.CreateSubdirectory("models", "sales"));
    }

    private static string Rule(string id, int severity)
        => $"[{{\"ID\":\"{id}\",\"Name\":\"{id.ToLowerInvariant()}\",\"Category\":\"c\",\"Severity\":{severity},\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}}]";

    [Fact]
    public void FindUpward_ReturnsNearestFirst_AndStopsAtTheRepositoryRoot()
    {
        using var outside = new TempDir();
        outside.WriteFile("tomix.json", "{}");
        var root = outside.CreateSubdirectory("repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "tomix.json"), "{}");
        var model = outside.CreateSubdirectory("repo", "models", "sales");
        File.WriteAllText(Path.Combine(model, "tomix.json"), "{}");

        var found = ProjectFiles.FindUpward(model, "tomix.json");

        Assert.Equal([Path.Combine(model, "tomix.json"), Path.Combine(root, "tomix.json")], found);
    }

    [Fact]
    public void FindUpward_TreatsAGitFileAsTheRoot()
    {
        // A worktree or submodule has a .git file, not a folder.
        using var outside = new TempDir();
        outside.WriteFile("tomix.json", "{}");
        var root = outside.CreateSubdirectory("worktree");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");

        Assert.Empty(ProjectFiles.FindUpward(root, "tomix.json"));
    }

    [Fact]
    public void Resolve_WithoutAnyFile_IsTheDefault()
    {
        var (root, model) = Repository();
        using var _ = root;

        var settings = BpaProjectConfig.Resolve(model, NoEnvironment);

        Assert.Same(BpaProjectSettings.None, settings);
        Assert.True(settings.Enabled);
        Assert.Empty(settings.Attribution);
    }

    [Fact]
    public void Resolve_NearerFileOverridesOnlyTheKeysItSets()
    {
        var (root, model) = Repository();
        using var _ = root;
        root.WriteFile("tomix.json", """{ "bpa": { "enabled": false, "failOn": "warning", "disabledRuleIds": ["ROOT_RULE"] } }""");
        root.WriteFile(Path.Combine("models", "sales", "tomix.json"), """{ "bpa": { "enabled": true, "disabledRuleIds": ["MODEL_RULE"] } }""");

        var settings = BpaProjectConfig.Resolve(model, NoEnvironment);

        Assert.True(settings.Enabled);
        Assert.Equal("warning", settings.FailOn);
        Assert.Contains(root.Combine("tomix.json"), settings.FailOnSetting);
        Assert.Equal(["MODEL_RULE", "ROOT_RULE"], settings.DisabledRuleIds.Order());
        Assert.Equal([Path.Combine(model, "tomix.json"), root.Combine("tomix.json")], settings.Files);
    }

    [Fact]
    public void Resolve_EnvironmentFileIsTheLowestLayer()
    {
        var (root, model) = Repository();
        using var _ = root;
        using var elsewhere = new TempDir();
        var env = elsewhere.WriteFile("ci.json", """{ "bpa": { "failOn": "warning", "enabled": false } }""");
        root.WriteFile("tomix.json", """{ "bpa": { "failOn": "error" } }""");

        var settings = BpaProjectConfig.Resolve(
            model, name => name == BpaProjectConfig.EnvironmentVariable ? env : null);

        Assert.Equal("error", settings.FailOn);
        Assert.False(settings.Enabled);
        Assert.Contains(env, settings.EnabledSetting);
        Assert.Equal([root.Combine("tomix.json"), env], settings.Files);
    }

    [Fact]
    public void Resolve_RulesLoadFarthestFirst_AnchoredAtTheirFile()
    {
        var (root, model) = Repository();
        using var _ = root;
        root.WriteFile("tomix.json", """{ "bpa": { "rules": ["bpa/team.json", "https://example.com/rules.json"] } }""");
        root.WriteFile(Path.Combine("models", "sales", "tomix.json"), """{ "bpa": { "rules": ["local.json"] } }""");

        var settings = BpaProjectConfig.Resolve(model, NoEnvironment);

        Assert.Equal(
            [root.Combine("bpa", "team.json"), "https://example.com/rules.json", Path.Combine(model, "local.json")],
            settings.Rules.Select(r => r.Location));
        Assert.All(settings.Rules, r => Assert.Equal(BpaRuleOrigin.Project, r.Origin));
    }

    [Fact]
    public void Resolve_AcceptsCommentsTrailingCommasAndAnyKeyCase_AndIgnoresOtherSections()
    {
        var (root, model) = Repository();
        using var _ = root;
        root.WriteFile("tomix.json", """
            {
              // a later feature's section
              "format": { "lines": "long" },
              "BPA": { "FailOn": "warning", },
            }
            """);

        Assert.Equal("warning", BpaProjectConfig.Resolve(model, NoEnvironment).FailOn);
    }

    [Theory]
    [InlineData("""{ "bpa": { "failOn": "info" } }""", "Invalid 'bpa.failOn' value 'info'")]
    [InlineData("""{ "bpa": { "failsOn": "warning" } }""", "failsOn")]
    [InlineData("""{ "bpa": { "enabled": "no" } }""", "invalid")]
    [InlineData("""{ "bpa": """, "invalid")]
    public void Resolve_InvalidFile_FailsAndNamesIt(string json, string expected)
    {
        var (root, model) = Repository();
        using var _ = root;
        var path = root.WriteFile("tomix.json", json);

        var ex = Assert.Throws<BpaProjectConfigException>(() => BpaProjectConfig.Resolve(model, NoEnvironment));

        Assert.Contains(path, ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Resolve_MissingEnvironmentFile_NamesTheVariable()
    {
        var (root, model) = Repository();
        using var _ = root;
        var missing = root.Combine("missing.json");

        var ex = Assert.Throws<BpaProjectConfigException>(() => BpaProjectConfig.Resolve(
            model, name => name == BpaProjectConfig.EnvironmentVariable ? missing : null));

        Assert.Contains(BpaProjectConfig.EnvironmentVariable, ex.Message);
    }

    [Fact]
    public void StartDirectory_IsTheFolderOfAModelFile_AndTheModelFolderItself()
    {
        using var root = new TempDir();
        var folder = root.CreateSubdirectory("model");
        var file = root.WriteFile(Path.Combine("bim", "model.bim"), "{}");

        Assert.Equal(folder, BpaProjectConfig.StartDirectory(new ModelReference(folder)));
        Assert.Equal(root.Combine("bim"), BpaProjectConfig.StartDirectory(new ModelReference(file)));
        Assert.Equal(Environment.CurrentDirectory,
            BpaProjectConfig.StartDirectory(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Sales")));
    }

    [Fact]
    public async Task BpaRun_AppliesTheProjectRulesDisablesAndThreshold_AndNamesTheFile()
    {
        var (root, _) = Repository();
        using var __ = root;
        using var config = new TempConfigDir();
        var model = SampleModel.CopyTo(root, Path.Combine("models", "sales", "model"));
        root.WriteFile(Path.Combine("bpa", "team.json"), Rule("TEAM_WARNING", severity: 2));
        root.WriteFile(Path.Combine("bpa", "noisy.json"), Rule("TEAM_NOISY", severity: 2));
        var tomixJson = root.WriteFile("tomix.json", """
            { "bpa": { "rules": ["bpa/team.json", "bpa/noisy.json"], "disabledRuleIds": ["team_noisy"], "failOn": "warning" } }
            """);

        var result = await new BpaRunHandler(
                [new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path,
                environment: NoEnvironment)
            .HandleAsync(new BpaRunRequest(new ModelReference(model), NoDefaults: true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data!.Violations, v => v.RuleId == "TEAM_WARNING");
        Assert.DoesNotContain(result.Data.Violations, v => v.RuleId == "TEAM_NOISY");
        Assert.Contains(result.Data.RuleSources, s => s.Origin == BpaRuleOrigin.Project);
        var loaded = Assert.Single(result.Diagnostics, d => d.Code == BpaProjectConfig.LoadedCode);
        Assert.Equal(DiagnosticSeverity.Info, loaded.Severity);
        Assert.Contains(tomixJson, loaded.Message);

        // --fail-on on the command line wins over the file's threshold.
        var flagged = await new BpaRunHandler(
                [new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path,
                environment: NoEnvironment)
            .HandleAsync(new BpaRunRequest(new ModelReference(model), NoDefaults: true, FailOn: "error"), CancellationToken.None);
        Assert.Equal(0, flagged.ExitCode);
    }

    [Fact]
    public async Task RulesList_ShowsTheProjectRulesAndDisables()
    {
        var (root, _) = Repository();
        using var __ = root;
        using var config = new TempConfigDir();
        var model = SampleModel.CopyTo(root, Path.Combine("models", "sales", "model"));
        root.WriteFile(Path.Combine("bpa", "team.json"), Rule("TEAM_WARNING", severity: 2));
        root.WriteFile("tomix.json", """
            { "bpa": { "rules": ["bpa/team.json"], "disabledRuleIds": ["HIDE_FOREIGN_KEYS"] } }
            """);

        var result = await new BpaRulesListHandler(
                [new TmdlModelProvider()], new BpaUserRuleState(config.Path), configDirectory: config.Path,
                environment: NoEnvironment)
            .HandleAsync(new BpaRulesListRequest(new ModelReference(model), All: true), CancellationToken.None);

        Assert.True(result.Success);
        var team = Assert.Single(result.Data!.Rules, r => r.Id == "TEAM_WARNING");
        Assert.Equal(BpaProjectConfig.FileName, team.Source);
        Assert.Equal("disabled", Assert.Single(result.Data.Rules, r => r.Id == "HIDE_FOREIGN_KEYS").Status);
    }

    [Fact]
    public async Task RulesList_BrokenProjectConfig_IsReportedNotFatal()
    {
        var (root, _) = Repository();
        using var __ = root;
        using var config = new TempConfigDir();
        var model = SampleModel.CopyTo(root, Path.Combine("models", "sales", "model"));
        var tomixJson = root.WriteFile("tomix.json", """{ "bpa": """);

        var result = await new BpaRulesListHandler(
                [new TmdlModelProvider()], new BpaUserRuleState(config.Path), configDirectory: config.Path,
                environment: NoEnvironment)
            .HandleAsync(new BpaRulesListRequest(new ModelReference(model)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Data!.Rules);
        Assert.Contains(result.Data.Diagnostics!, d => d.Contains(tomixJson));
    }

    [Fact]
    public async Task Save_FixBpa_AppliesTheProjectRulesAndDisables()
    {
        var (root, _) = Repository();
        using var __ = root;
        var model = SampleModel.CopyTo(root, Path.Combine("models", "sales", "model"));
        // A rule that cannot be evaluated always blocks a save, whatever else the fixes did.
        root.WriteFile(Path.Combine("bpa", "team.json"),
            Rule("TEAM_ERROR", severity: 3).Replace("\"Expression\":\"true\"", "\"Expression\":\"ThisIsNotARealMember = 1\""));
        root.WriteFile("tomix.json", """{ "bpa": { "rules": ["bpa/team.json"] } }""");

        var blocked = await SaveWithFixAsync(model, root.Combine("out-blocked"));
        Assert.False(blocked.Success);
        Assert.Equal("TOMIX_BPA_VIOLATIONS", blocked.Diagnostics[0].Code);

        root.WriteFile("tomix.json", """{ "bpa": { "rules": ["bpa/team.json"], "disabledRuleIds": ["TEAM_ERROR"] } }""");
        var saved = await SaveWithFixAsync(model, root.Combine("out-saved"));
        Assert.True(saved.Success, string.Join("; ", saved.Diagnostics.Select(d => d.Message)));
    }

    private static Task<Core.Results.TomixResult<SaveModelResult>> SaveWithFixAsync(string model, string output)
        => new SaveModelHandler([new TmdlModelProvider()], environment: NoEnvironment).HandleAsync(
            new SaveModelRequest(new ModelReference(model), output, "", Overwrite: true, SupportingFiles: false, FixBpa: true),
            CancellationToken.None);
}
