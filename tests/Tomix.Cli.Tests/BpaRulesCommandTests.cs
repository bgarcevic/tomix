using System.Text.Json;
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
