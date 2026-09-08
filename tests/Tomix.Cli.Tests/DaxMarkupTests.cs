using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

public sealed class DaxMarkupTests
{
    [Fact]
    public void DaxMarkup_HighlightsRolesWithPaletteColors()
    {
        var markup = Styling.DaxMarkup("SUM(Sales[Amount]) + 0.5 // check");

        Assert.Contains($"[{Palette.Harbor.ToMarkup()}]SUM[/]", markup);
        Assert.Contains($"[{Palette.Sage.ToMarkup()}]Sales[/]", markup);
        Assert.Contains($"[{Palette.Moss.ToMarkup()}][[Amount]][/]", markup);
        Assert.Contains($"[{Palette.Amber.ToMarkup()}]0.5[/]", markup);
        Assert.Contains($"[{Palette.Slate.ToMarkup()}]// check[/]", markup);
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
        Assert.Contains($"[{Palette.Moss.ToMarkup()}][[red]][/]", markup);
        Assert.Contains($"[{Palette.Sage.ToMarkup()}]Bold[/]", markup);
    }

    [Fact]
    public void DaxMarkup_DefinitionName_IsBold()
    {
        var markup = Styling.DaxMarkup("Total Amount := SUM(Sales[Amount])");

        Assert.Contains("[bold]Total[/] [bold]Amount[/] :=", markup);
    }
}
