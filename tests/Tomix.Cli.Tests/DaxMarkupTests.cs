using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

public sealed class DaxMarkupTests
{
    [Fact]
    public void DaxMarkup_HighlightsRolesWithPaletteColors()
    {
        var markup = Styling.DaxMarkup(
            "VAR x = SUM(Sales[Amount]) + [Profit] * 0.5 // check\nRETURN x",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit" });

        Assert.Contains($"[{Palette.Keyword.ToMarkup()}]VAR[/]", markup);
        Assert.Contains($"[{Palette.Keyword.ToMarkup()}]RETURN[/]", markup);
        Assert.Contains($"[{Palette.Function.ToMarkup()}]SUM[/]", markup);
        Assert.Contains($"[{Palette.Reference.ToMarkup()}][[Profit]][/]", markup);
        Assert.Contains($"[{Palette.Literal.ToMarkup()}]0.5[/]", markup);
        Assert.Contains("[dim]// check[/]", markup);
    }

    [Fact]
    public void DaxMarkup_TablesColumnsAndVariables_StayPlain()
    {
        var markup = Styling.DaxMarkup("VAR x = SUM(Sales[Amount]) RETURN x");

        Assert.Contains("(Sales[[Amount]])", markup);
        Assert.EndsWith("[/] x", markup);
    }

    [Fact]
    public void DaxMarkup_WrapsUnstyledTextEscapedAndPlain()
    {
        var markup = Styling.DaxMarkup("SUM(Sales[Amount]) + 1");

        // Operators and whitespace sit between colored spans, escaped but unstyled.
        Assert.Contains(") + [", markup);
    }

    [Fact]
    public void DaxMarkup_DaxBracketLookalikes_AreEscapedInsideTheirSpan()
    {
        var markup = Styling.DaxMarkup("[red]Bold[/]");

        // [red] is a column reference whose text is escaped, so it renders literally.
        Assert.Equal("[red]Bold[/]", Spectre.Console.Markup.Remove(markup));
    }

    [Fact]
    public void DaxMarkup_DefinitionName_IsBold()
    {
        var markup = Styling.DaxMarkup("Total Amount := SUM(Sales[Amount])");

        Assert.Contains("[bold]Total[/] [bold]Amount[/] :=", markup);
    }
}
