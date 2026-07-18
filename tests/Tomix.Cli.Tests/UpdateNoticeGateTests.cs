using System.CommandLine;
using Tomix.Cli;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Update;

namespace Tomix.Cli.Tests;

public sealed class UpdateNoticeGateTests
{
    private static bool ShouldShow(
        string outputFormat = "text",
        bool quiet = false,
        bool stderrRedirected = false,
        bool ciEnv = false,
        bool envOptOut = false,
        bool configOptOut = false,
        InstallKind kind = InstallKind.Standalone,
        string version = "0.2.0")
        => UpdateNotice.ShouldShow(outputFormat, quiet, stderrRedirected, ciEnv, envOptOut, configOptOut, kind, version);

    [Fact]
    public void DefaultInteractiveTextRun_Shows()
    {
        Assert.True(ShouldShow());
        Assert.True(ShouldShow(kind: InstallKind.DotnetTool));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("tmdl")]
    public void NonTextOutput_Suppresses(string format)
    {
        Assert.False(ShouldShow(outputFormat: format));
    }

    [Fact]
    public void QuietSuppresses() => Assert.False(ShouldShow(quiet: true));

    [Fact]
    public void RedirectedStderrSuppresses() => Assert.False(ShouldShow(stderrRedirected: true));

    [Fact]
    public void CiEnvironmentSuppresses() => Assert.False(ShouldShow(ciEnv: true));

    [Fact]
    public void EnvOptOutSuppresses() => Assert.False(ShouldShow(envOptOut: true));

    [Fact]
    public void ConfigOptOutSuppresses() => Assert.False(ShouldShow(configOptOut: true));

    [Theory]
    [InlineData(InstallKind.Development)]
    [InlineData(InstallKind.Unknown)]
    public void NonUpdatableInstallSuppresses(InstallKind kind)
    {
        Assert.False(ShouldShow(kind: kind));
    }

    [Fact]
    public void MissingVersionSuppresses() => Assert.False(ShouldShow(version: "0.0.0"));

    // ── Output-format resolution ────────────────────────────────────────────
    // Commands define their own local --output-format which shadows the recursive
    // global option; the notice gate must see the value the command actually used
    // (Codex review finding on PR #63: 'tx doctor --output-format json' still noticed).

    private static ParseResult ParseWithLocalFormatCommand(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new DoctorCommand("0.1.0", TestServices.Create().ConfigDirectory).Build());
        return root.Parse(args);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public void ResolveOutputFormat_ReadsTheCommandsLocalOption(string format)
    {
        var parseResult = ParseWithLocalFormatCommand("doctor", "--output-format", format);

        Assert.Equal(format, UpdateNotice.ResolveOutputFormat(parseResult));
    }

    [Fact]
    public void ResolveOutputFormat_DefaultsToText_WhenTheOptionIsOmitted()
    {
        var parseResult = ParseWithLocalFormatCommand("doctor");

        Assert.Equal(OutputFormats.Text, UpdateNotice.ResolveOutputFormat(parseResult));
    }

    [Fact]
    public void ResolveOutputFormat_FallsBackToTheGlobalOption_ForCommandsWithoutALocalOne()
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new Command("bare"));

        var parseResult = root.Parse(["bare", "--output-format", "json"]);

        Assert.Equal("json", UpdateNotice.ResolveOutputFormat(parseResult));
    }
}
