using System.Text.RegularExpressions;
using Tomix.Ui;

namespace Tomix.Cli.Tests;

/// <summary>
/// The <c>tx ui</c> page keeps its colors in one token block: every color it uses is a token
/// defined there, and the accent is redefined for dark mode so it keeps its contrast.
/// </summary>
public sealed partial class UiPagePaletteTests
{
    [Fact]
    public void EveryTokenThePageUses_IsDefined()
    {
        var page = Page();
        var defined = TokenDefinition().Matches(page).Select(match => match.Groups[1].Value).ToHashSet();

        var undefined = TokenUse().Matches(page)
            .Select(match => match.Groups[1].Value)
            .Where(token => !defined.Contains(token))
            .Distinct();

        Assert.Empty(undefined);
    }

    [Fact]
    public void Accent_IsRedefinedForDarkMode()
    {
        var page = Page();
        var dark = page[page.IndexOf("prefers-color-scheme:dark", StringComparison.Ordinal)..];

        Assert.Contains("--accent:", dark, StringComparison.Ordinal);
        Assert.Contains("--on-accent:", dark, StringComparison.Ordinal);
    }

    [Fact]
    public void Logo_WearsTheAccent()
        => Assert.Contains(".logo{width:22px;height:22px;border-radius:6px;background:var(--accent);color:var(--on-accent);", Page(), StringComparison.Ordinal);

    [GeneratedRegex(@"--([a-z-]+):")]
    private static partial Regex TokenDefinition();

    [GeneratedRegex(@"var\(--([a-z-]+)\)")]
    private static partial Regex TokenUse();

    private static string Page()
    {
        using var stream = typeof(UiHost).Assembly.GetManifestResourceStream("Tomix.Ui.Page.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
