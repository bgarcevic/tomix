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
        string version = "0.2.0",
        bool reportsUpdateStatus = false)
        => UpdateNotice.ShouldShow(outputFormat, quiet, stderrRedirected, ciEnv, envOptOut, configOptOut, kind, version, reportsUpdateStatus);

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

    // ── Commands that report the update status themselves ───────────────────
    // A successful in-process update leaves the running assembly's version stale
    // while the check step has just cached the new one, so the end-of-command
    // notice announced the very update that had just been applied (seen on the
    // 0.1.0 -> 0.2.0 binary swap). `doctor` already reports the cached update as a
    // check, so the notice would print the same news twice.

    [Fact]
    public void SelfReportingCommandSuppresses() => Assert.False(ShouldShow(reportsUpdateStatus: true));

    [Theory]
    [InlineData("update")]
    [InlineData("update", "--check")]
    [InlineData("doctor")]
    public void SelfReportingInvocation_IsDetected(params string[] args)
    {
        var root = TestRoot.With(UpdateWithCheck(), new Command("doctor"), new Command("connect"));

        Assert.True(UpdateNotice.ReportsUpdateStatus(root.Parse(args)));
    }

    [Fact]
    public void OtherInvocation_IsNotDetected()
    {
        var root = TestRoot.With(UpdateWithCheck(), new Command("doctor"), new Command("connect"));

        Assert.False(UpdateNotice.ReportsUpdateStatus(root.Parse(["connect"])));
        Assert.False(UpdateNotice.ReportsUpdateStatus(root.Parse([])));
    }

    private static Command UpdateWithCheck() => new("update") { new Option<bool>("--check") };

    // ── Output-format resolution ────────────────────────────────────────────
    // Commands define their own local --output-format which shadows the recursive
    // global option; the notice gate must see the value the command actually used
    // (Codex review finding on PR #63: 'tx doctor --output-format json' still noticed).

    private static ParseResult ParseWithLocalFormatCommand(params string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new DoctorCommand(
            "0.1.0", services.ConfigDirectory, services.ConfigStore, services.State,
            services.UpdateCheck, Path.Combine(services.ConfigDirectory, "auth")).Build());
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
        var root = TestRoot.With(new Command("bare"));

        var parseResult = root.Parse(["bare", "--output-format", "json"]);

        Assert.Equal("json", UpdateNotice.ResolveOutputFormat(parseResult));
    }
}
