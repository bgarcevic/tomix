using Spectre.Console;
using Spectre.Console.Testing;
using Tomix.App.Session;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>The welcome screen of <c>tx interactive</c>, with and without an open model.</summary>
public sealed class InteractiveBannerTests
{
    private static readonly SessionModelInfo Model = new(
        "./try-model", Path.GetTempPath(), new ModelSummary("try-model", 1601, 3, 12, 5, 2, 0));

    [Fact]
    public void WithAModel_ShowsItsNameCountsAndWhereItSaves()
    {
        var output = Render(Model, width: 120);

        Assert.Contains("████████╗", output);
        Assert.Contains("T O M I X", output);
        Assert.Contains("v1.2.3", output);
        Assert.Contains("Model: try-model", output);
        Assert.Contains("TMDL folder · compatibility level 1601", output);
        Assert.Contains("3 tables  12 columns  5 measures  2 relationships  0 roles", output);
        Assert.Contains($"Saves to: {Path.GetTempPath()}", output);
        Assert.DoesNotContain("No model open", output);
        Assert.Contains("Tip:", output);
    }

    [Fact]
    public void WithoutAModel_SaysHowToOpenOne()
    {
        var output = Render(null, width: 120);

        Assert.Contains("No model open. connect <path> opens a TMDL folder or .bim file", output);
        Assert.DoesNotContain("Model:", output);
    }

    [Theory]
    [InlineData(40, true, false)]
    [InlineData(18, false, false)]
    [InlineData(120, true, true)]
    public void NarrowTerminals_DropTheTitleBesideTheLogo_ThenTheLogo(int width, bool logo, bool beside)
    {
        var output = Render(Model, width);

        Assert.Equal(logo, output.Contains("████████╗", StringComparison.Ordinal));
        Assert.Equal(beside, output.Contains("T O M I X", StringComparison.Ordinal));
        Assert.Contains("v1.2.3", output);
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd().Length <= width, $"Wider than {width}: {line}"));
    }

    [Fact]
    public void Logo_BlocksAreBoldWithoutColor_OutlineDim()
    {
        var console = new TestConsole().Width(120).Colors(ColorSystem.Standard).EmitAnsiSequences();
        InteractiveBanner.Write(console, "1.2.3", Model, new Random(1));

        // No color: the terminal's own foreground looks right in every theme.
        Assert.Contains("\x1b[1m█", console.Output);
        Assert.DoesNotContain("\x1b[38;5;", console.Output);
        Assert.Contains("\x1b[2m╗", console.Output);
    }

    private static string Render(SessionModelInfo? model, int width)
    {
        var console = new TestConsole().Width(width);
        InteractiveBanner.Write(console, "1.2.3", model, new Random(1));
        return console.Output;
    }
}
