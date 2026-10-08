using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

public sealed class MMarkupTests
{
    [Fact]
    public void MMarkup_HighlightsRolesWithPaletteColors()
    {
        var markup = Styling.MMarkup("let Source = Table.SelectRows(T, each [Amount] > 0.5) // check\nin Source");

        Assert.Contains($"[{Palette.Keyword.ToMarkup()}]let[/] Source =", markup);
        Assert.Contains($"[{Palette.Function.ToMarkup()}]Table.SelectRows[/]", markup);
        Assert.Contains($"[{Palette.Keyword.ToMarkup()}]each[/] [[Amount]] >", markup);
        Assert.Contains($"[{Palette.Literal.ToMarkup()}]0.5[/]", markup);
        Assert.Contains("[dim]// check[/]", markup);
    }

    [Theory]
    [InlineData("[red]Bold[/]")]
    [InlineData("\"[bold]\" & [x = \"[/]\"]")]
    [InlineData("/* [link=https://example.com] */ 1")]
    public void MMarkup_BracketsNeverInjectMarkup(string m)
    {
        var markup = Styling.MMarkup(m);

        // Spectre parses the markup back to exactly the source text: nothing was interpreted.
        Assert.Equal(m, Spectre.Console.Markup.Remove(markup));
    }

    [Fact]
    public void ExpressionMarkup_Plain_EscapesWithoutStyling()
    {
        Assert.Equal("let [[x]]", Styling.ExpressionMarkup(ExpressionLanguage.Plain, "let [x]"));
    }
}
