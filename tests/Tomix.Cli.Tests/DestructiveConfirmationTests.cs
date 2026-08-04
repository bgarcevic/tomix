using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Destructive commands must refuse to run without confirmation when prompting is impossible,
/// and <c>--yes</c> must bypass the prompt for scripts. Confirmation goes through the single
/// gate-aware <see cref="ConfirmationHelper.ConfirmOrAbort"/> overload, so this covers every
/// caller: <c>session clear</c>/<c>prune</c>, <c>stage discard</c>, <c>rm</c>, <c>replace</c>,
/// <c>deploy</c>, <c>incremental-refresh rm</c>, and the <c>connect</c> workspace overwrite.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DestructiveConfirmationTests
{
    private static RootCommand BuildRoot(IReadOnlyList<IModelProvider>? providers = null)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        var services = TestServices.Create();
        var noProviders = providers ?? Array.Empty<IModelProvider>();
        root.Subcommands.Add(new SessionCommand(services.State).Build());
        root.Subcommands.Add(new StageCommand(noProviders, services.State, services.Staging).Build());
        root.Subcommands.Add(new RmCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new ReplaceCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new DeployCommand(noProviders, services.State).Build());
        root.Subcommands.Add(new IncrementalRefreshCommand(
            noProviders, services.State, services.Mutations, services.LoadCurrentSession).Build());
        root.Subcommands.Add(new ConnectCommand(noProviders, FakeWorkspaceCatalog.Empty, () => null, services.State).Build());
        return root;
    }

    private static (int ExitCode, string Stdout, string Stderr) Invoke(params string[] args)
        => Invoke(BuildRoot(), args);

    private static (int ExitCode, string Stdout, string Stderr) Invoke(RootCommand root, string[] args)
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
    [InlineData("session", "clear")]
    [InlineData("session", "prune")]
    [InlineData("session", "prune", "--all")]
    [InlineData("stage", "discard")]
    [InlineData("stage", "discard", "--all")]
    [InlineData("rm", "SomeTable")]
    [InlineData("replace", "foo", "bar")]
    [InlineData("deploy", "model.bim")]
    [InlineData("incremental-refresh", "rm", "SomeTable")]
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
    [InlineData("rm", "SomeTable", "--quiet")]
    [InlineData("replace", "foo", "bar", "--quiet")]
    [InlineData("deploy", "model.bim", "--quiet")]
    [InlineData("incremental-refresh", "rm", "SomeTable", "--quiet")]
    [InlineData("session", "clear", "--output-format", "json")]
    [InlineData("session", "prune", "--output-format", "json")]
    [InlineData("stage", "discard", "--output-format", "json")]
    [InlineData("rm", "SomeTable", "--output-format", "json")]
    [InlineData("replace", "foo", "bar", "--output-format", "json")]
    [InlineData("deploy", "model.bim", "--output-format", "json")]
    [InlineData("incremental-refresh", "rm", "SomeTable", "--output-format", "json")]
    public void WithoutYes_NonPromptableContext_AbortsWithGuidance(params string[] args)
    {
        var (exitCode, _, stderr) = Invoke(args);

        Assert.Equal(1, exitCode);
        Assert.Contains("Pass --yes to confirm", stderr);
    }

    // The connect workspace-overwrite confirmation sits behind a successful model open and a
    // mirror probe that finds the target dataset, so it needs a provider that can "open"
    // anything. The gate must still fail fast in non-promptable contexts.
    [Theory]
    [InlineData("--non-interactive")]
    [InlineData("--quiet")]
    [InlineData("--output-format", "json")]
    public void ConnectWorkspaceOverwrite_WithoutYes_NonPromptableContext_AbortsWithGuidance(
        params string[] contextArgs)
    {
        var model = Directory.CreateTempSubdirectory("tomix-confirm-connect-").FullName;
        try
        {
            var (exitCode, _, stderr) = Invoke(
                BuildRoot([new OpenAnythingProvider()]),
                ["connect", model, "SalesDataset", "-w", "AnalyticsWorkspace", .. contextArgs]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Pass --yes to confirm Overwrite workspace target", stderr);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
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

    private sealed class OpenAnythingProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new SummaryOnlySession(reference.Value));
    }

    private sealed class SummaryOnlySession(string sourcePath) : IModelSession
    {
        public string SourcePath => sourcePath;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("Test", 1600, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
