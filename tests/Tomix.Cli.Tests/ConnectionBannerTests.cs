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
public sealed class ConnectionBannerTests
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
}
