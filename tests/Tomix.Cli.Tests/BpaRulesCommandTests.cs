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
}
