using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>stage discard</c>, <c>session clear</c>, and <c>session prune</c> are destructive, so they
/// must refuse to run without confirmation when prompting is impossible, and <c>--yes</c> must
/// bypass the prompt for scripts.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DestructiveConfirmationTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        var services = TestServices.Create();
        root.Subcommands.Add(new SessionCommand(services.State).Build());
        root.Subcommands.Add(new StageCommand(Array.Empty<IModelProvider>(), services.State, services.Staging).Build());
        return root;
    }

    private static (int ExitCode, string Stdout, string Stderr) Invoke(params string[] args)
    {
        var result = BuildRoot().Parse(args);
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
    [InlineData("session", "clear")]
    [InlineData("session", "prune")]
    [InlineData("session", "prune", "--all")]
    [InlineData("stage", "discard")]
    [InlineData("stage", "discard", "--all")]
    public void WithoutYes_NonInteractive_AbortsWithGuidance(params string[] args)
    {
        var (exitCode, _, stderr) = Invoke([.. args, "--non-interactive"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Pass --yes to confirm", stderr);
    }

    // Confirmation goes through InteractionGate, so every non-promptable context —
    // not just --non-interactive — must fail fast instead of blocking on a prompt.
    [Theory]
    [InlineData("session", "clear", "--quiet")]
    [InlineData("session", "prune", "--quiet")]
    [InlineData("stage", "discard", "--quiet")]
    [InlineData("session", "clear", "--output-format", "json")]
    [InlineData("session", "prune", "--output-format", "json")]
    [InlineData("stage", "discard", "--output-format", "json")]
    public void WithoutYes_NonPromptableContext_AbortsWithGuidance(params string[] args)
    {
        var (exitCode, _, stderr) = Invoke(args);

        Assert.Equal(1, exitCode);
        Assert.Contains("Pass --yes to confirm", stderr);
    }

    // Success paths assert JSON output on purpose: AnsiConsole-backed text output caches
    // the console writer from the first invoke, so captured text is unreliable across invokes.
    [Fact]
    public void SessionClear_WithYes_Proceeds()
    {
        var (exitCode, stdout, _) = Invoke("session", "clear", "--yes", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"cleared\": false", stdout);
    }

    [Fact]
    public void SessionPrune_WithYes_Proceeds()
    {
        var (exitCode, stdout, _) = Invoke("session", "prune", "--yes", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"removed\": 0", stdout);
    }

    [Fact]
    public void SessionPrune_DryRun_NeedsNoConfirmation()
    {
        var (exitCode, stdout, _) = Invoke(
            "session", "prune", "--dry-run", "--non-interactive", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"dryRun\": true", stdout);
    }

    [Fact]
    public void StageDiscard_WithYes_Proceeds()
    {
        var model = Path.Combine(Path.GetTempPath(), "tomix-cli-tests-nonexistent-model");
        var (exitCode, stdout, _) = Invoke(
            "stage", "discard", "--yes", "--model", model, "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"discarded\": 0", stdout);
    }
}
