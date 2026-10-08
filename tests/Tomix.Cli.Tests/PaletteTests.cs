using System.Reflection;
using Spectre.Console;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The palette contract: every role is an ANSI-16 palette index, so the user's terminal theme
/// picks the shade, and it stays a 4-bit code even on a true-color terminal (an RGB escape would
/// bypass the theme). Only colors that read well across common themes are allowed, and the DAX
/// roles that share a line never share a color. Rationale lives in docs/cli-color-strategy.md.
/// </summary>
public sealed class PaletteTests
{
    // Red, green, yellow, magenta, cyan, and bright blue. Normal blue (4) is unreadable on the
    // Windows Terminal default; the other bright codes (9-15) turn grey in Solarized.
    private static readonly Color[] Allowed = [.. new[] { 1, 2, 3, 5, 6, 12 }.Select(Color.FromInt32)];

    public static TheoryData<string, int> RoleIndexes => new()
    {
        { nameof(Palette.Error), 1 },
        { nameof(Palette.Success), 2 },
        { nameof(Palette.Warning), 3 },
        { nameof(Palette.Info), 6 },
        { nameof(Palette.Keyword), 5 },
        { nameof(Palette.Function), 12 },
        { nameof(Palette.Reference), 6 },
        { nameof(Palette.Literal), 2 },
    };

    [Fact]
    public void EveryRole_IsAnAllowedAnsi16Color()
    {
        foreach (var (name, color) in PaletteColors())
            Assert.True(Allowed.Contains(color),
                $"{name} is {color.ToMarkup()}, not one of the ANSI-16 colors the palette allows");
    }

    [Theory]
    [MemberData(nameof(RoleIndexes))]
    public void Role_RendersAsAPaletteIndexOnATrueColorTerminal(string role, int index)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
        });

        console.Write(new Text("x", new Style(PaletteColors()[role])));

        Assert.StartsWith($"\x1b[38;5;{index}m", output.ToString());
    }

    [Fact]
    public void DaxRoles_NeverShareAColor()
    {
        Color[] roles = [Palette.Keyword, Palette.Function, Palette.Reference, Palette.Literal];

        Assert.Equal(roles.Length, roles.Distinct().Count());
    }

    private static Dictionary<string, Color> PaletteColors()
        => typeof(Palette)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Color))
            .ToDictionary(field => field.Name, field => (Color)field.GetValue(null)!);
}
