using System.CommandLine;
using Tomix.Cli;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Deploy QA finding: the contradictory-flag rejections exited 2 with the right message but
/// never emitted the JSON error envelope, because <c>deploy</c> was one of the commands that
/// dropped <c>--error-format</c> on the floor. Exit codes and codes are the scripting contract
/// (see <c>docs/guides/scripting.md</c>), so a pipeline branching on
/// <c>TOMIX_DEPLOY_INVALID_FLAGS</c> saw nothing to branch on.
///
/// The rejection itself lives in the CLI layer (it needs <c>Implicit</c> to tell an explicit
/// <c>--deploy-connections false</c> from an unset flag), so the handler tests cannot cover it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DeployFlagRejectionTests
{
    [Fact]
    public void DeployFull_WithGranularFlag_IsRejectedWithExitTwo()
    {
        var (exitCode, stderr) = Invoke(
            "deploy", "model.bim", "--deploy-full", "--deploy-connections", "--yes", "--non-interactive");

        Assert.Equal(2, exitCode);
        Assert.Contains("--deploy-full cannot be combined", stderr);
    }

    [Fact]
    public void DeployFull_WithGranularFlag_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var (exitCode, stderr) = Invoke(
            "deploy", "model.bim", "--deploy-full", "--deploy-connections", "--yes", "--non-interactive",
            "--error-format", "json");

        Assert.Equal(2, exitCode);
        using var doc = System.Text.Json.JsonDocument.Parse(stderr);
        Assert.Equal("TOMIX_DEPLOY_INVALID_FLAGS", doc.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// An explicit <c>--deploy-full</c> alone must survive: the guard keys off granular flags
    /// being explicitly present, so a bug that treated their defaults as present would reject
    /// every full deploy. Reaching the target is what fails here, not the flag validation.
    /// </summary>
    [Fact]
    public void DeployFull_Alone_IsNotRejectedAsInvalidFlags()
    {
        var (_, stderr) = Invoke(
            "deploy", "model.bim", "--deploy-full", "--yes", "--non-interactive",
            "--error-format", "json");

        Assert.DoesNotContain("TOMIX_DEPLOY_INVALID_FLAGS", stderr);
    }

    private static (int ExitCode, string Stderr) Invoke(params string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new DeployCommand([], services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(root.Parse(args));
        return (captured.ExitCode, captured.Stderr);
    }
}
