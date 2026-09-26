using Tomix.App.Bpa;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

public class BpaRulesRenderTests
{
    [Fact]
    public void ShowHint_PreservesSelectedFileAndModel()
    {
        var request = new BpaRulesListRequest(
            Model: new ModelReference("models/Sales Model.bim"),
            RulesFile: "rules/Custom Rules.json",
            NoDefaults: true);

        Assert.Equal(
            "tx bpa rules --rules-file 'rules/Custom Rules.json' show CUSTOM_RULE 'models/Sales Model.bim' --no-defaults",
            BpaRulesRenderer.ShowHint("CUSTOM_RULE", request));
    }

    [Fact]
    public void ShowHint_PreservesRemoteDatabaseAndRuleset()
    {
        var request = new BpaRulesListRequest(
            Model: new ModelReference("powerbi://example/workspace", "Sales Model"),
            Ruleset: "full");

        Assert.Equal(
            "tx bpa rules show 'RULE_$X' powerbi://example/workspace --database 'Sales Model' --ruleset full",
            BpaRulesRenderer.ShowHint("RULE_$X", request));
    }

    [Fact]
    public void ExpressionLines_JoinsLoneOperatorsAndDropsBlankLines()
    {
        var expression = "IsAvailableInMDX\r\nand\r\n\n(IsHidden or Table.IsHidden)\r\nand\r\n  not UsedInSortBy.Any() \r\n";

        Assert.Equal(
            ["IsAvailableInMDX", "and (IsHidden or Table.IsHidden)", "and not UsedInSortBy.Any()"],
            BpaRulesRenderer.ExpressionLines(expression));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  \r\n ")]
    public void ExpressionLines_Empty_ReturnsNothing(string? expression)
        => Assert.Empty(BpaRulesRenderer.ExpressionLines(expression));

    [Fact]
    public void SplitReference_SeparatesTrailingLink()
    {
        var (text, reference) = BpaRulesRenderer.SplitReference("Hide it.\r\nReference: https://example.com/x");

        Assert.Equal("Hide it.", text);
        Assert.Equal("https://example.com/x", reference);
    }

    [Theory]
    [InlineData("Just guidance.", "Just guidance.")]
    [InlineData(null, "")]
    public void SplitReference_NoLink_KeepsText(string? description, string expected)
    {
        var (text, reference) = BpaRulesRenderer.SplitReference(description);

        Assert.Equal(expected, text);
        Assert.Null(reference);
    }
}
