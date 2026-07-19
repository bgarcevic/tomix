using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Update;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class UpdateCommandParseTests
{
    private static RootCommand BuildRoot(FakeReleaseSource? source = null, string version = "0.1.0")
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        var services = TestServices.Create();
        root.Subcommands.Add(new UpdateCommand(version, source ?? FakeReleaseSource.Empty, services.UpdateCheck).Build());
        return root;
    }

    private static (int ExitCode, string Stdout, string Stderr) Invoke(RootCommand root, params string[] args)
    {
        var result = root.Parse(args);
        Assert.Empty(result.Errors);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (result.Invoke(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("update")]
    [InlineData("update", "--check")]
    [InlineData("update", "--version", "0.2.0")]
    [InlineData("update", "--check", "--version", "0.2.0", "--output-format", "json")]
    public void ValidInvocations_ParseWithoutErrors(params string[] args)
        => Assert.Empty(BuildRoot().Parse(args).Errors);

    [Fact]
    public void Check_Json_EmitsTheDocumentedContract()
    {
        var source = new FakeReleaseSource
        {
            Releases =
            [
                new ReleaseInfo("0.3.0", "v0.3.0", "* feat!: rename option", PublishedAt: null, Prerelease: false),
                new ReleaseInfo("0.2.0", "v0.2.0", "* feat: minor", PublishedAt: null, Prerelease: false),
            ]
        };

        var (exitCode, stdout, _) = Invoke(BuildRoot(source), "update", "--check", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"currentVersion\": \"0.1.0\"", stdout);
        Assert.Contains("\"latestVersion\": \"0.3.0\"", stdout);
        Assert.Contains("\"updateAvailable\": true", stdout);
        Assert.Contains("\"installKind\":", stdout);
        Assert.Contains("\"breaking\": true", stdout);
    }

    [Fact]
    public void Check_ExitsZero_EvenWhenUpToDate()
    {
        var source = new FakeReleaseSource
        {
            Releases = [new ReleaseInfo("0.1.0", "v0.1.0", null, PublishedAt: null, Prerelease: false)]
        };

        var (exitCode, stdout, _) = Invoke(BuildRoot(source), "update", "--check", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"updateAvailable\": false", stdout);
    }

    [Fact]
    public void Check_WithoutReleases_ExitsOneWithCheckFailed()
    {
        var (exitCode, _, stderr) = Invoke(
            BuildRoot(), "update", "--check", "--error-format", "json");

        Assert.Equal(1, exitCode);
        Assert.Contains("TOMIX_UPDATE_CHECK_FAILED", stderr);
    }

    [Fact]
    public void PinnedUnpublishedVersion_ExitsTwoWithVersionNotFound()
    {
        var source = new FakeReleaseSource
        {
            Releases = [new ReleaseInfo("0.3.0", "v0.3.0", null, PublishedAt: null, Prerelease: false)]
        };

        var (exitCode, _, stderr) = Invoke(
            BuildRoot(source), "update", "--check", "--version", "9.9.9", "--error-format", "json");

        Assert.Equal(2, exitCode);
        Assert.Contains("TOMIX_UPDATE_VERSION_NOT_FOUND", stderr);
    }

    [Fact]
    public void Perform_InTestHost_FailsWithUnsupportedInstall()
    {
        // The test host runs from a build output directory, so the install kind is
        // Development and a real update must refuse to run.
        var source = new FakeReleaseSource
        {
            Releases = [new ReleaseInfo("0.3.0", "v0.3.0", null, PublishedAt: null, Prerelease: false)]
        };

        var (exitCode, _, stderr) = Invoke(
            BuildRoot(source), "update", "--yes", "--error-format", "json");

        Assert.Equal(2, exitCode);
        Assert.Contains("TOMIX_UPDATE_UNSUPPORTED_INSTALL", stderr);
    }
}
