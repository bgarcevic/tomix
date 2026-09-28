using System.CommandLine;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// The "Connected to:" banner: commands that resolve their model implicitly from the saved
/// active connection name it on stderr, so users always see which model is operated on.
/// Explicit targets (model path, --server, --recent) stay silent, and the banner never
/// reaches --quiet or machine output.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class ConnectionBannerTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    private static readonly string SampleTmdl = SampleModel.Locate();

    [Fact]
    public void Ls_WithActiveSession_AnnouncesTargetOnStderr()
    {
        var (root, _) = CreateRoot(withSessionModel: true);

        var captured = ConsoleCapture.Invoke(root.Parse(["ls"]));

        Assert.Equal(0, captured.ExitCode);
        Assert.Contains("Connected to:", captured.Stderr);
        Assert.Contains(SampleTmdl, captured.Stderr);
        // The listing itself stays on stdout; the banner must not pollute it.
        Assert.DoesNotContain("Connected to:", captured.Stdout);
    }

    [Fact]
    public void Announce_RedirectedStderr_KeepsALongTargetOnOneLine()
    {
        // Redirected stderr has no terminal width, so Spectre used to hard-wrap at 80 columns and
        // split the path — the banner test above failed whenever the repo lived at a long path.
        var longPath = Path.Combine(Path.GetTempPath(), string.Join(" ", Enumerable.Repeat("long folder name", 12)), "model");
        var parsed = TestRoot.With(new LsCommand(Providers, TestServices.Create().State).Build()).Parse(["ls"]);

        var captured = ConsoleCapture.Run(() => ConnectionBanner.Announce(parsed, new ModelReference(longPath)));

        Assert.Equal($"Connected to: {longPath}", StripAnsi(captured.Stderr).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Ls_WithExplicitModel_DoesNotAnnounce()
    {
        var (root, _) = CreateRoot(withSessionModel: true);

        var captured = ConsoleCapture.Invoke(root.Parse(["ls", SampleTmdl]));

        Assert.Equal(0, captured.ExitCode);
        Assert.DoesNotContain("Connected to:", captured.Stderr);
    }

    [Fact]
    public void Ls_Quiet_DoesNotAnnounce()
    {
        var (root, _) = CreateRoot(withSessionModel: true);

        var captured = ConsoleCapture.Invoke(root.Parse(["ls", "--quiet"]));

        Assert.Equal(0, captured.ExitCode);
        Assert.DoesNotContain("Connected to:", captured.Stderr);
    }

    [Theory]
    [InlineData("--output-format", "json")]
    [InlineData("--output-format", "csv")]
    public void Ls_MachineOutput_DoesNotAnnounce(params string[] formatArgs)
    {
        var (root, _) = CreateRoot(withSessionModel: true);

        var captured = ConsoleCapture.Invoke(root.Parse(["ls", .. formatArgs]));

        Assert.Equal(0, captured.ExitCode);
        Assert.DoesNotContain("Connected to:", captured.Stderr);
    }

    [Fact]
    public void Ls_WithoutActiveSession_DoesNotAnnounce()
    {
        var (root, _) = CreateRoot(withSessionModel: false);

        var captured = ConsoleCapture.Invoke(root.Parse(["ls", SampleTmdl]));

        Assert.Equal(0, captured.ExitCode);
        Assert.DoesNotContain("Connected to:", captured.Stderr);
    }

    // --- Power BI Desktop sessions --------------------------------------------------------------

    private static CliConnectionState DesktopSession(string? reportName = "Sales Overview")
        => new("localhost:65034", "31ca6303", Model: null, Auth: null, Local: true, Profile: null,
            ReportName: reportName, ReportPortFile: reportName is null ? null : "port.txt");

    private static readonly ModelReference DesktopReference = new("localhost:65034", "31ca6303");

    [Fact]
    public void Render_DesktopSession_NamesTheReport()
    {
        var line = StripMarkup(ConnectionBanner.Render(DesktopReference, DesktopSession(), _ => true, (_, _) => true));

        Assert.Equal("Connected to: Sales Overview  (localhost:65034)", line);
    }

    [Fact]
    public void Render_DesktopSession_StaleReportName_FallsBackToTheEndpoint()
    {
        var line = StripMarkup(ConnectionBanner.Render(DesktopReference, DesktopSession(), _ => true, (_, _) => false));

        Assert.Equal("Connected to: localhost:65034 / 31ca6303", line);
    }

    [Theory]
    [InlineData("Sales Overview", "Connected to: Sales Overview (not running)")]
    [InlineData(null, "Connected to: localhost:65034 / 31ca6303 (not running)")]
    public void Render_ClosedDesktopInstance_SaysSoAndHowToRecover(string? reportName, string expectedStart)
    {
        var line = StripMarkup(ConnectionBanner.Render(
            DesktopReference, DesktopSession(reportName), _ => false, (_, _) => true));

        Assert.StartsWith(expectedStart, line, StringComparison.Ordinal);
        Assert.Contains("tx connect --local", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NonDesktopTarget_NeverProbes()
    {
        var line = ConnectionBanner.Render(
            new ModelReference(SampleTmdl),
            new CliConnectionState(null, null, SampleTmdl, null, true, null),
            _ => throw new InvalidOperationException("must not probe"),
            (_, _) => throw new InvalidOperationException("must not probe"));

        Assert.Equal($"Connected to: {SampleTmdl}", StripMarkup(line));
    }

    private static string StripMarkup(string markup) => Spectre.Console.Markup.Remove(markup);

    private static (RootCommand Root, CliStateStore State) CreateRoot(bool withSessionModel)
    {
        var services = TestServices.Create();
        if (withSessionModel)
        {
            services.State.SaveCurrentSession(new CliConnectionState(
                Server: null, Database: null, Model: SampleTmdl,
                Auth: null, Local: true, Profile: null));
        }

        return (TestRoot.With(new LsCommand(Providers, services.State).Build()), services.State);
    }

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}
