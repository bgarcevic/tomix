using System.Reflection;
using Spectre.Console;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The perceptual contract of the palette, from color-theory practice: every role must be
/// readable on both dark and light terminal backgrounds (WCAG-style contrast ratio), and roles
/// that appear in the same view must stay perceptually distant (CIELAB ΔE) so hue alone never
/// carries a distinction. Construction rationale lives in docs/cli-color-strategy.md.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class PaletteTests
{
    // Dark and light stand-ins for the two background families terminals ship with.
    private const string DarkBackground = "#1E1E1E";
    private const string LightBackground = "#FFFFFF";

    /// <summary>Roles that render next to each other in the same view must not drift together.</summary>
    private static readonly (string Left, string Right, string Where)[] SameViewPairs =
    [
        ("Harbor", "Sage", "DAX: functions vs tables"),
        ("Sage", "Moss", "DAX: tables vs columns"),
        ("Moss", "Orchid", "DAX: columns vs measures"),
        ("Orchid", "Lav", "DAX: measures vs keywords"),
        ("Terra", "Amber", "DAX: variables vs literals"),
        ("Slate", "Terra", "DAX: comments vs variables"),
        ("Rose", "Moss", "reports: errors vs success"),
        ("Amber", "Rose", "reports: warnings vs errors"),
    ];

    [Fact]
    public void EveryRole_IsReadableOnDarkAndLightBackgrounds()
    {
        foreach (var (name, color) in PaletteColors())
        {
            Assert.True(ContrastRatio(color, DarkBackground) >= 3.2,
                $"{name} falls below 3.2:1 on {DarkBackground}");
            Assert.True(ContrastRatio(color, LightBackground) >= 3.2,
                $"{name} falls below 3.2:1 on {LightBackground}");
        }
    }

    [Fact]
    public void SameViewRoles_StayPerceptuallyDistinct()
    {
        foreach (var (left, right, where) in SameViewPairs)
        {
            var distance = DeltaE(PaletteColors()[left], PaletteColors()[right]);
            Assert.True(distance >= 25, $"{where}: {left} and {right} are too close (ΔE {distance:0.0} < 25).");
        }
    }

    private static Dictionary<string, Rgb> PaletteColors()
        => typeof(Palette)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Color))
            .ToDictionary(
                field => field.Name,
                field => new Rgb(((Color)field.GetValue(null)!).R, ((Color)field.GetValue(null)!).G, ((Color)field.GetValue(null)!).B));

    private readonly record struct Rgb(double R, double G, double B);

    private static double ContrastRatio(Rgb color, string backgroundHex)
    {
        var (colorLum, backgroundLum) = OrderedLuminance(color, ParseHex(backgroundHex));
        return (backgroundLum + 0.05) / (colorLum + 0.05);
    }

    private static double Luminance(Rgb color)
    {
        double Channel(double channel)
            => channel / 255 <= 0.04045 ? channel / 255 / 12.92 : Math.Pow((channel / 255 + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static (double Darker, double Lighter) OrderedLuminance(Rgb color, Rgb background)
    {
        var colorLum = Luminance(color);
        var backgroundLum = Luminance(background);
        return colorLum < backgroundLum ? (colorLum, backgroundLum) : (backgroundLum, colorLum);
    }

    /// <summary>CIELAB ΔE76 — the simple perceptual distance; adequate for relative comparisons.</summary>
    private static double DeltaE(Rgb left, Rgb right)
    {
        var (l, a, b) = ToLab(left);
        var (l2, a2, b2) = ToLab(right);
        return Math.Sqrt(Math.Pow(l - l2, 2) + Math.Pow(a - a2, 2) + Math.Pow(b - b2, 2));
    }

    private static (double L, double A, double B) ToLab(Rgb color)
    {
        double Channel(double channel)
            => channel / 255 <= 0.04045 ? channel / 255 / 12.92 : Math.Pow((channel / 255 + 0.055) / 1.055, 2.4);
        var (r, g, b) = (Channel(color.R), Channel(color.G), Channel(color.B));

        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        var y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;

        double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (841.0 / 108) * t + 4.0 / 29;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    private static Rgb ParseHex(string hex)
        => new(
            Convert.ToInt32(hex[1..3], 16),
            Convert.ToInt32(hex[3..5], 16),
            Convert.ToInt32(hex[5..7], 16));
}
