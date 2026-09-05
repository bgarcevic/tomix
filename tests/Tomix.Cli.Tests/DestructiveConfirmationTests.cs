using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Destructive commands must refuse to run without confirmation when prompting is impossible,
/// and <c>--yes</c> must bypass the prompt for scripts. Confirmation goes through the single
/// gate-aware <see cref="ConfirmationHelper.ConfirmOrAbort"/> overload, so this covers every
/// caller: <c>session clear</c>/<c>prune</c>, <c>stage commit</c>/<c>discard</c>, <c>rm</c>,
/// <c>replace</c>, <c>deploy</c>, <c>incremental-refresh rm</c>, the <c>connect</c> workspace
/// overwrite, the partition-risky <c>refresh</c> variants, <c>script --save</c>/<c>--revert</c>,
/// <c>mv --save</c>/<c>--revert</c>, and <c>bpa run --fix --allow-delete</c>/<c>--revert</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DestructiveConfirmationTests
{
    private const string RemoteEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/TestWorkspace";

    private static RootCommand BuildRoot(IReadOnlyList<IModelProvider>? providers = null)
    {
        var services = TestServices.Create();
        var noProviders = providers ?? Array.Empty<IModelProvider>();
        var root = TestRoot.With(new SessionCommand(services.State).Build());
        root.Subcommands.Add(new StageCommand(noProviders, services.State, services.Staging).Build());
        root.Subcommands.Add(new RmCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new ReplaceCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new DeployCommand(noProviders, services.State).Build());
        root.Subcommands.Add(new IncrementalRefreshCommand(
            noProviders, services.State, services.Mutations, services.LoadCurrentSession).Build());
        root.Subcommands.Add(new ConnectCommand(noProviders, FakeWorkspaceCatalog.Empty, () => null, services.State).Build());
        root.Subcommands.Add(new RefreshCommand(noProviders, services.State, services.LoadCurrentSession).Build());
        root.Subcommands.Add(new ScriptCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new MvCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new BpaCommand(
            noProviders, services.State, services.Mutations, services.BpaRules, services.ConfigDirectory).Build());
        return root;
    }

    private static (int ExitCode, string Stdout, string Stderr) Invoke(params string[] args)
        => Invoke(BuildRoot(), args);

    private static (int ExitCode, string Stdout, string Stderr) Invoke(RootCommand root, string[] args)
    {
        var result = root.Parse(args);
        Assert.Empty(result.Errors);

        var captured = ConsoleCapture.Invoke(result);
        return (captured.ExitCode, captured.Stdout, captured.Stderr);
    }

    [Theory]
    [InlineData("session", "clear")]
    [InlineData("session", "prune")]
    [InlineData("session", "prune", "--all")]
    [InlineData("stage", "discard")]
    [InlineData("stage", "discard", "--all")]
    [InlineData("stage", "commit", "--model", "SomeModel")]
    [InlineData("rm", "SomeTable")]
    [InlineData("replace", "foo", "bar")]
    [InlineData("deploy", "model.bim")]
    [InlineData("incremental-refresh", "rm", "SomeTable")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--type", "clearvalues")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--skip-refresh-policy")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--effective-date", "2026-01-01")]
    [InlineData("script", "--model", "SomeModel", "--save")]
    [InlineData("script", "--model", "SomeModel", "--revert")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--revert")]
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
    [InlineData("stage", "commit", "--model", "SomeModel", "--quiet")]
    [InlineData("rm", "SomeTable", "--quiet")]
    [InlineData("replace", "foo", "bar", "--quiet")]
    [InlineData("deploy", "model.bim", "--quiet")]
    [InlineData("incremental-refresh", "rm", "SomeTable", "--quiet")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--type", "clearvalues", "--quiet")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--skip-refresh-policy", "--quiet")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--effective-date", "2026-01-01", "--quiet")]
    [InlineData("script", "--model", "SomeModel", "--save", "--quiet")]
    [InlineData("script", "--model", "SomeModel", "--revert", "--quiet")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save", "--quiet")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert", "--quiet")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--quiet")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--revert", "--quiet")]
    [InlineData("session", "clear", "--output-format", "json")]
    [InlineData("session", "prune", "--output-format", "json")]
    [InlineData("stage", "discard", "--output-format", "json")]
    [InlineData("stage", "commit", "--model", "SomeModel", "--output-format", "json")]
    [InlineData("rm", "SomeTable", "--output-format", "json")]
    [InlineData("replace", "foo", "bar", "--output-format", "json")]
    [InlineData("deploy", "model.bim", "--output-format", "json")]
    [InlineData("incremental-refresh", "rm", "SomeTable", "--output-format", "json")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--type", "clearvalues", "--output-format", "json")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--skip-refresh-policy", "--output-format", "json")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--effective-date", "2026-01-01", "--output-format", "json")]
    [InlineData("script", "--model", "SomeModel", "--save", "--output-format", "json")]
    [InlineData("script", "--model", "SomeModel", "--revert", "--output-format", "json")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save", "--output-format", "json")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert", "--output-format", "json")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--output-format", "json")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--revert", "--output-format", "json")]
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
            // The message names what needs confirming; the hint is the remediation. Asserted
            // separately so a future edit cannot quietly turn the hint back into a restatement
            // of the action, which is what it was.
            Assert.Contains("Overwrite workspace target", stderr);
            Assert.Contains("needs confirmation", stderr);
            Assert.Contains("Pass --yes to confirm", stderr);
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

    [Fact]
    public void StageCommit_WithYes_ProceedsPastGate()
    {
        var model = Path.Combine(Path.GetTempPath(), "tomix-cli-tests-nonexistent-model");
        var (exitCode, _, stderr) = Invoke(
            "stage", "commit", "--yes", "--model", model, "--non-interactive", "--output-format", "json");

        // The gate let the invocation through: the failure is the handler's nothing-staged
        // diagnostic, not TOMIX_CONFIRMATION_REQUIRED.
        Assert.Equal(1, exitCode);
        Assert.Contains("Nothing staged to commit", stderr);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    // Routine refreshes never prompt — only the partition-risky variants do — so a plain
    // refresh reaches the handler (and fails there, since no provider can open the endpoint).
    [Fact]
    public void Refresh_WithoutRiskyFlags_NeedsNoConfirmation()
    {
        var (exitCode, _, stderr) = Invoke(
            "refresh", "-s", RemoteEndpoint, "-d", "Sales", "--non-interactive", "--output-format", "json");

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    [Fact]
    public void Refresh_DryRun_ClearValues_NeedsNoConfirmation()
    {
        var (exitCode, _, stderr) = Invoke(
            "refresh", "-s", RemoteEndpoint, "-d", "Sales", "--type", "clearvalues",
            "--dry-run", "--non-interactive", "--output-format", "json");

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    // Only the persisting forms ask: without --save/--save-to/--stage/--revert the mutation
    // stays in memory, and --stage defers the gate to 'stage commit'.
    [Fact]
    public void Script_WithoutSaveOrRevert_NeedsNoConfirmation()
    {
        var (exitCode, _, stderr) = Invoke(
            "script", "--model", "SomeModel", "--non-interactive", "--output-format", "json");

        // The handler's own validation ("No scripts specified") runs once the gate is passed.
        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    [Fact]
    public void Mv_WithoutSaveOrRevert_NeedsNoConfirmation()
    {
        var (exitCode, _, stderr) = Invoke(
            "mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--non-interactive", "--output-format", "json");

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    [Fact]
    public void BpaRun_FixWithoutAllowDelete_NeedsNoConfirmation()
    {
        var (exitCode, _, stderr) = Invoke(
            "bpa", "run", "--model", "SomeModel", "--fix", "--non-interactive", "--output-format", "json");

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
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
