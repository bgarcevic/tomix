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
