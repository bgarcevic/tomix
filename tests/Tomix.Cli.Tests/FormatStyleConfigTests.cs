using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Core.Configuration;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx format</c> reads its style from the <c>format.*</c> config keys, each flag overrides its
/// key, and EU separators never reach the model: asked for explicitly with a write they are
/// rejected, and from config the model still stores the US form (#229).
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatStyleConfigTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];
    private const string Ratio = "divide(sum(Sales[Amount]),1.5)";

    [Fact]
    public void ConfigStyle_AppliesToInlineFormat()
    {
        var services = ServicesWith(eu: true, noSpace: true);

        var output = Run(services, ["format", "-e", Ratio]);

        Assert.Equal("DIVIDE( SUM( Sales[Amount] ); 1,5 )", output.Stdout.Trim());
    }

    [Fact]
    public void Flags_OverrideConfigStyle()
    {
        var services = ServicesWith(eu: true, noSpace: true);

        var output = Run(services, ["format", "-e", Ratio, "--separators", "us", "--function-spacing", "space"]);

        Assert.Equal("DIVIDE ( SUM ( Sales[Amount] ), 1.5 )", output.Stdout.Trim());
    }

    [Fact]
    public void ShortFlag_OverridesLongLinesFromConfig()
    {
        var services = TestServices.Create();
        services.ConfigStore.Save(new Dictionary<string, string> { [ConfigKeys.FormatLines] = "long" });
        const string wide = "SUMX(FILTER(Sales, Sales[Price] > 1 && Sales[Region] = \"West\"), Sales[Qty] * Sales[Price])";

        Assert.Single(Lines(Run(services, ["format", "-e", wide]).Stdout));
        Assert.True(Lines(Run(services, ["format", "-e", wide, "--short"]).Stdout).Length > 1);
    }

    [Fact]
    public void LongAndShort_Together_AreAUsageError()
    {
        var captured = Run(TestServices.Create(), ["format", "-e", Ratio, "--long", "--short", "--output-format", "json"], expectExit: 2);

        using var json = JsonDocument.Parse(captured.Stderr);
        Assert.Equal("TOMIX_USAGE", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void UnknownSeparators_IsRejectedAtParse()
        => Assert.Contains(
            Root(TestServices.Create()).Parse(["format", "-e", Ratio, "--separators", "fr"]).Errors,
            error => error.Message.Contains("Known values: us, eu", StringComparison.Ordinal));

    [Theory]
    [InlineData("--save")]
    [InlineData("--stage")]
    public void ExplicitEuSeparators_WithAWrite_AreRejectedAndNothingIsWritten(string write)
    {
        using var model = ModelWithRatio();
        var before = File.ReadAllText(SalesFile(model));

        var captured = Run(
            TestServices.Create(),
            ["format", "--path", "Sales/Ratio", "-m", model.Path, write, "--separators", "eu", "--output-format", "json"],
            expectExit: 2);

        using var json = JsonDocument.Parse(captured.Stderr);
        Assert.Equal("TOMIX_FORMAT_SEPARATORS_DISPLAY_ONLY", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllText(SalesFile(model)));
    }

    [Fact]
    public void EuSeparatorsFromConfig_ShowEu_ButTheModelStoresUs()
    {
        using var model = ModelWithRatio();
        var services = ServicesWith(eu: true, noSpace: true);

        var captured = Run(services, ["format", "--path", "Sales/Ratio", "-m", model.Path, "--save"]);

        Assert.Contains("DIVIDE( SUM( Sales[Amount] ); 1,5 )", captured.Stdout);
        var saved = File.ReadAllText(SalesFile(model));
        Assert.Contains("DIVIDE( SUM( Sales[Amount] ), 1.5 )", saved);
        Assert.DoesNotContain(";", saved);
    }

    private static AppServices ServicesWith(bool eu, bool noSpace)
    {
        var services = TestServices.Create();
        var config = new Dictionary<string, string>();
        if (eu) config[ConfigKeys.FormatSeparators] = "eu";
        if (noSpace) config[ConfigKeys.FormatFunctionSpacing] = "none";
        services.ConfigStore.Save(config);
        return services;
    }

    private static TempDir ModelWithRatio()
    {
        var model = SampleModel.CopyToTemp();
        File.AppendAllText(SalesFile(model), $"\n\tmeasure Ratio = {Ratio}\n");
        return model;
    }

    private static string SalesFile(TempDir model) => Path.Combine(model.Path, "tables", "Sales.tmdl");

    private static ConsoleCapture.Captured Run(AppServices services, string[] args, int expectExit = 0)
    {
        var root = Root(services);
        // Wide enough that the console never soft-wraps a long line.
        var captured = ConsoleCapture.Run(
            () =>
            {
                AnsiConsole.Profile.Width = 200;
                return root.Parse(args).Invoke();
            },
            captureAnsiConsole: true);
        Assert.True(captured.ExitCode == expectExit, $"format exited {captured.ExitCode}: {captured.Stderr}");
        return captured with { Stdout = AnsiRegex().Replace(captured.Stdout, "") };
    }

    private static RootCommand Root(AppServices services)
        => TestRoot.With(new FormatCommand(
            Providers,
            new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient()]),
            services.State,
            services.Mutations,
            config: services.ConfigStore).Build());

    private static string[] Lines(string text)
        => text.ReplaceLineEndings("\n").Trim().Split('\n');

    [GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
}
