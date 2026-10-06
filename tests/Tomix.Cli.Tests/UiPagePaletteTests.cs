using Spectre.Console;
using Tomix.Cli.Output;
using Tomix.Ui;

namespace Tomix.Cli.Tests;

/// <summary>The <c>tx ui</c> page wears the CLI's palette: a change to one shows up in the other.</summary>
public sealed class UiPagePaletteTests
{
    public static TheoryData<string, string> Colors => new()
    {
        { "harbor", Hex(Palette.Harbor) },
        { "lav", Hex(Palette.Lav) },
        { "moss", Hex(Palette.Moss) },
        { "amber", Hex(Palette.Amber) },
        { "rose", Hex(Palette.Rose) },
        { "slate", Hex(Palette.Slate) }
    };

    [Theory]
    [MemberData(nameof(Colors))]
    public void PageColor_IsThePaletteColor(string name, string hex)
        => Assert.Contains($"--{name}:{hex};", Page(), StringComparison.Ordinal);

    [Fact]
    public void Logo_IsShadedLikeTheWelcomeBanner()
        => Assert.Contains("linear-gradient(90deg,var(--harbor),var(--lav))", Page(), StringComparison.Ordinal);

    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private static string Page()
    {
        using var stream = typeof(UiHost).Assembly.GetManifestResourceStream("Tomix.Ui.Page.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
