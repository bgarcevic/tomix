using System.CommandLine;
using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.App.Format;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the text <c>tx format</c> prints for each style knob (#229), so a printer refactor cannot
/// silently change the formatting grammar people copy back into models. The sample carries what a
/// naive separator swap would break: commas and decimals inside strings and comments, a table
/// constructor, decimals, and a call without arguments. Regenerate with
/// <c>TOMIX_UPDATE_FORMAT_GOLDENS=1</c> and review the diff.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatStyleGoldenTests
{
    private const string Dax =
        "VAR Threshold = 1.5 -- rate, as 1.5\n" +
        "RETURN SUMX(FILTER(Sales, Sales[Price] > Threshold && Sales[Region] IN {\"West, North\", \"East\"}), " +
        "Sales[Qty] * Sales[Price] * 0.25) + DIVIDE(COUNTROWS(Sales), YEAR(NOW()), 0)";

    private const string M =
        "let Source = Table.FromRows({{1, \"a, b\"}, {2.5, \"c\"}}, {\"Id\", \"Name\"}), " +
        "Filtered = Table.SelectRows(Source, each [Id] > 1) in Filtered";

    [Theory]
    [InlineData("format-dax-default")]
    [InlineData("format-dax-eu", "--separators", "eu")]
    [InlineData("format-dax-no-space", "--function-spacing", "none")]
    [InlineData("format-dax-long", "--long")]
    [InlineData("format-dax-eu-no-space-long", "--separators", "eu", "--function-spacing", "none", "--long")]
    public void Dax_StyleMatrix_MatchesGolden(string golden, params string[] style)
        => AssertGolden(golden, Format(["format", "-e", Dax, .. style]));

    [Theory]
    [InlineData("format-m-default")]
    [InlineData("format-m-long", "--long")]
    public void M_MatchesGolden(string golden, params string[] style)
        => AssertGolden(golden, Format(["format", "-e", M, "--lang", "m", .. style]));

    private static string Format(string[] args)
    {
        // Wider than any long line, so the console does not soft-wrap what the golden pins.
        var root = BuildRoot();
        var captured = ConsoleCapture.Run(
            () =>
            {
                AnsiConsole.Profile.Width = 200;
                return root.Parse(args).Invoke();
            },
            captureAnsiConsole: true);
        Assert.True(captured.ExitCode == 0, $"format exited {captured.ExitCode}: {captured.Stderr}");
        return AnsiRegex().Replace(captured.Stdout, "").ReplaceLineEndings("\n");
    }

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new FormatCommand(
            [],
            new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient(), new OfflineMFormatterClient()]),
            services.State,
            services.Mutations).Build());
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = RepoPaths.Combine("tests", "Tomix.Cli.Tests", "Goldens", $"{name}.approved.txt");
        if (Environment.GetEnvironmentVariable("TOMIX_UPDATE_FORMAT_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing format golden: {path}. Run with TOMIX_UPDATE_FORMAT_GOLDENS=1 and review it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
}
