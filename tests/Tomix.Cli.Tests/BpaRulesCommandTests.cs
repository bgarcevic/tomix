using System.Text.Json;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class BpaRulesCommandTests
{
    private const string SelectedRuleJson =
        "[{\"ID\":\"SELECTED_RULE\",\"Name\":\"Selected rule\",\"Category\":\"Custom\","
        + "\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\"}]";

    [Fact]
    public void Disable_SelectedRulesFile_AcceptsItsRuleId()
    {
        using var dir = new TempDir();
        var rulesFile = dir.WriteFile("selected.json", SelectedRuleJson);
        var root = TestRoot.Full();

        var result = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "--rules-file", rulesFile, "disable", "SELECTED_RULE"]),
            captureAnsiConsole: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SELECTED_RULE", result.Stdout);
    }

    [Fact]
    public void IgnoreUser_RoundTrip_SwitchesTheRuleForTheUser()
    {
        using var dir = new TempDir();
        var rulesFile = dir.WriteFile("selected.json", SelectedRuleJson);
        var root = TestRoot.Full();

        JsonElement Run(string verb)
        {
            var result = ConsoleCapture.Invoke(root.Parse(
                ["bpa", "rules", "--rules-file", rulesFile, verb, "SELECTED_RULE", "--user", "--output-format", "json"]));
            Assert.Equal(0, result.ExitCode);
            return JsonDocument.Parse(result.Stdout).RootElement.GetProperty("data");
        }

        var ignored = Run("ignore");
        Assert.Equal("user", ignored.GetProperty("level").GetString());
        Assert.True(ignored.GetProperty("disabled").GetBoolean());
        Assert.Contains("SELECTED_RULE", ignored.GetProperty("disabledRuleIds").EnumerateArray().Select(e => e.GetString()));

        var unignored = Run("unignore");
        Assert.False(unignored.GetProperty("disabled").GetBoolean());
        Assert.True(unignored.GetProperty("changed").GetBoolean());
    }

    [Theory]
    [InlineData("ignore", "--save")]
    [InlineData("unignore", "--stage")]
    [InlineData("ignore", "some-model-folder")]
    public void IgnoreUser_WithAModelOrSaveOption_IsAConflict(string verb, string extra)
    {
        var result = ConsoleCapture.Invoke(TestRoot.Full().Parse(
            ["bpa", "rules", verb, "HIDE_FOREIGN_KEYS", "--user", extra, "--output-format", "json"]));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("\"TOMIX_OPTION_CONFLICT\"", result.Stderr);
    }

    [Fact]
    public void Authoring_RoundTrip_InitAddListSetRemove()
    {
        using var dir = new TempDir();
        var rulesFile = dir.Combine("team.json");
        var root = TestRoot.Full();

        int Run(params string[] args)
            => ConsoleCapture.Invoke(root.Parse(["bpa", "rules", "--rules-file", rulesFile, .. args]),
                captureAnsiConsole: true).ExitCode;

        Assert.Equal(0, Run("init"));
        Assert.Equal(0, Run("add", "--id", "MY_RULE", "--name", "Hide keys", "--scope", "Column, Measure",
            "--expression", "IsKey", "--severity", "error"));
        Assert.Equal(2, Run("add", "--id", "MY_RULE", "--name", "dup", "--scope", "Table", "--expression", "true"));

        var list = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "--rules-file", rulesFile, "list", "--no-defaults"]), captureAnsiConsole: true);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("MY_RULE", list.Stdout);
        Assert.Contains("Column, Measure", list.Stdout);

        var set = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "--rules-file", rulesFile, "set", "MY_RULE", "--severity", "info", "--output-format", "json"]));
        Assert.Equal(0, set.ExitCode);
        Assert.Contains("\"changedFields\"", set.Stdout);
        Assert.Contains("\"severityLabel\": \"Info\"", set.Stdout);

        Assert.Equal(0, Run("remove", "MY_RULE"));
        Assert.Equal(2, Run("remove", "MY_RULE"));
    }

    [Fact]
    public void Add_WithoutId_IsAParseError()
    {
        var result = ConsoleCapture.Invoke(TestRoot.Full().Parse(
            ["bpa", "rules", "add", "--name", "N", "--scope", "Table", "--expression", "true"]));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void List_SelectedRulesFile_ShowsContextualShowHint()
    {
        using var dir = new TempDir();
        var rulesFile = dir.WriteFile("selected.json", SelectedRuleJson);
        var root = TestRoot.Full();

        var result = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "--rules-file", rulesFile, "list", "--no-defaults"]),
            captureAnsiConsole: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("tx bpa rules --rules-file", result.Stderr);
        Assert.Contains("show SELECTED_RULE", result.Stderr);
        Assert.Contains("--no-defaults", result.Stderr);
    }

    [Fact]
    public void Authoring_ModelTarget_EditsTheAnnotation_NotTheRulesFile()
    {
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var root = TestRoot.With(new BpaCommand(
            [new TmdlModelProvider()], services.State, services.Mutations, services.BpaRules, services.ConfigDirectory).Build());

        var add = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "add", model.Path, "--id", "MY_RULE", "--name", "n", "--scope", "Table",
                "--expression", "true", "--save", "--output-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(0, add.ExitCode);
        using var json = JsonDocument.Parse(add.Stdout);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("add", data.GetProperty("action").GetString());
        Assert.Equal("model-embedded", data.GetProperty("rule").GetProperty("source").GetString());
        Assert.Equal("saved", data.GetProperty("status").GetString());
        Assert.False(data.TryGetProperty("path", out _));
        Assert.False(File.Exists(Path.Combine(services.ConfigDirectory, "bpa-rules.json")));

        var removed = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "remove", "MY_RULE", model.Path, "--save"]), captureAnsiConsole: true);
        Assert.Equal(0, removed.ExitCode);
        Assert.Contains("Removed model rule", removed.Stdout);
    }

    [Fact]
    public void List_NoModel_UsesTheLocalActiveConnection()
    {
        // `ignore --save` acts on the active model, so `list --ignored` must see that model too.
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        services.State.SaveCurrentSession(new CliConnectionState(
            Server: null, Database: null, Model: model.Path, Auth: null, Local: true, Profile: null));
        var root = TestRoot.With(new BpaCommand(
            [new TmdlModelProvider()], services.State, services.Mutations, services.BpaRules, services.ConfigDirectory).Build());

        var ignore = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "ignore", "HIDE_FOREIGN_KEYS", "--save"]), captureAnsiConsole: true);
        Assert.Equal(0, ignore.ExitCode);

        var list = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "list", "--ignored", "--output-format", "json"]));

        Assert.Equal(0, list.ExitCode);
        var rule = Assert.Single(JsonDocument.Parse(list.Stdout).RootElement.GetProperty("data").GetProperty("rules").EnumerateArray());
        Assert.Equal("HIDE_FOREIGN_KEYS", rule.GetProperty("id").GetString());
        Assert.True(rule.GetProperty("ignoredByModel").GetBoolean());

        var show = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "show", "HIDE_FOREIGN_KEYS", "--output-format", "json"]));

        Assert.Equal(0, show.ExitCode);
        var shown = Assert.Single(JsonDocument.Parse(show.Stdout).RootElement.GetProperty("data").GetProperty("rules").EnumerateArray());
        Assert.True(shown.GetProperty("ignoredByModel").GetBoolean());
    }

    [Fact]
    public void List_RepeatedRuleset_CombinesThePresets()
    {
        // PowerShell splits an unquoted `standard,full` into two arguments; repeating the option
        // combines presets with no quoting at all.
        var result = ConsoleCapture.Invoke(TestRoot.Full().Parse(
            ["bpa", "rules", "list", "--ruleset", "standard", "--ruleset", "full", "--output-format", "json"]));

        Assert.Equal(0, result.ExitCode);
        var ids = JsonDocument.Parse(result.Stdout).RootElement.GetProperty("data").GetProperty("rules")
            .EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("UNNECESSARY_COLUMNS", ids);
    }

    [Fact]
    public void Run_Ruleset_TakesOneValue_SoTheModelStaysPositional()
    {
        var parse = TestRoot.Full().Parse(["bpa", "run", "--ruleset", "full", "some-model"]);

        Assert.Empty(parse.Errors);
        Assert.Equal(["full"], parse.GetValue<string[]>("--ruleset")!);
    }

    [Fact]
    public void Authoring_ModelAndRulesFile_Conflict()
    {
        using var model = SampleModel.CopyToTemp();
        using var dir = new TempDir();
        var services = TestServices.Create();
        var root = TestRoot.With(new BpaCommand(
            [new TmdlModelProvider()], services.State, services.Mutations, services.BpaRules, services.ConfigDirectory).Build());

        var result = ConsoleCapture.Invoke(root.Parse(
            ["bpa", "rules", "--rules-file", dir.Combine("team.json"), "remove", "MY_RULE", model.Path,
                "--output-format", "json"]));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("TOMIX_BPA_RULES_TARGET_CONFLICT", result.Stderr);
    }
}
