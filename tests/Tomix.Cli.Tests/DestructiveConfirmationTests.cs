using System.CommandLine;
using Tomix.App;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Destructive commands must refuse to run without confirmation when prompting is impossible,
/// and <c>--yes</c> must bypass the prompt for scripts. Confirmation goes through the single
/// gate-aware <see cref="ConfirmationHelper.ConfirmOrAbort"/> overload, so this covers every
/// caller: <c>connect --clear --all</c>, <c>stage commit</c>/<c>discard</c>, the persisting
/// forms of <c>rm</c>, <c>replace</c>, <c>mv</c> and <c>bpa run --fix --allow-delete</c>, their
/// <c>--revert</c>, and the <c>connect</c> workspace overwrite. The preview-first commands
/// (<c>deploy</c> and the partition-risky <c>refresh</c> variants) go through
/// <see cref="PreviewGate"/> instead: without <c>--yes</c> they preview, then prompt or stop.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class DestructiveConfirmationTests
{
    private const string RemoteEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/TestWorkspace";

    private static RootCommand BuildRoot(IReadOnlyList<IModelProvider>? providers = null)
    {
        var services = TestServices.Create();
        var noProviders = providers ?? Array.Empty<IModelProvider>();
        var root = TestRoot.With(new StageCommand(noProviders, services.State, services.Staging).Build());
        root.Subcommands.Add(new RmCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new ReplaceCommand(noProviders, services.State, services.Mutations).Build());
        root.Subcommands.Add(new DeployCommand(noProviders, services.State).Build());
        root.Subcommands.Add(new ConnectCommand(noProviders, FakeWorkspaceCatalog.Empty, () => null, services.State).Build());
        root.Subcommands.Add(new RefreshCommand(noProviders, services.State, services.LoadCurrentSession).Build());
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
    [InlineData("connect", "--clear", "--all")]
    [InlineData("stage", "discard")]
    [InlineData("stage", "discard", "--all")]
    [InlineData("stage", "commit", "--model", "SomeModel")]
    [InlineData("rm", "SomeTable", "--save")]
    [InlineData("rm", "SomeTable", "--stage")]
    [InlineData("rm", "SomeTable", "--revert")]
    [InlineData("replace", "foo", "bar", "--save")]
    [InlineData("replace", "foo", "bar", "--save-to", "out.bim")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--save")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--stage")]
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
    [InlineData("connect", "--clear", "--all", "--quiet")]
    [InlineData("stage", "discard", "--quiet")]
    [InlineData("stage", "commit", "--model", "SomeModel", "--quiet")]
    [InlineData("rm", "SomeTable", "--save", "--quiet")]
    [InlineData("replace", "foo", "bar", "--save", "--quiet")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save", "--quiet")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert", "--quiet")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--save", "--quiet")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--revert", "--quiet")]
    [InlineData("connect", "--clear", "--all", "--output-format", "json")]
    [InlineData("stage", "discard", "--output-format", "json")]
    [InlineData("stage", "commit", "--model", "SomeModel", "--output-format", "json")]
    [InlineData("rm", "SomeTable", "--save", "--output-format", "json")]
    [InlineData("replace", "foo", "bar", "--save", "--output-format", "json")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--save", "--output-format", "json")]
    [InlineData("mv", "Sales/Old", "Sales/New", "--model", "SomeModel", "--revert", "--output-format", "json")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete", "--save", "--output-format", "json")]
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
    public void ConnectClearAll_WithYes_Proceeds()
    {
        var (exitCode, stdout, _) = Invoke("connect", "--clear", "--all", "--yes", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"removed\": 0", stdout);
    }

    [Fact]
    public void ConnectClear_WithoutAll_NeedsNoConfirmation()
    {
        var (exitCode, stdout, _) = Invoke("connect", "--clear", "--non-interactive", "--output-format", "json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"cleared\": false", stdout);
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

    // Routine refreshes run straight away; only the partition-risky variants preview first and
    // stop (exit 3) where they cannot prompt, without ever calling the engine.
    [Theory]
    [InlineData(false)]
    [InlineData(false, "--refresh-type", "full")]
    [InlineData(false, "--table", "Sales")]
    [InlineData(true, "--refresh-type", "clearvalues")]
    [InlineData(true, "--skip-refresh-policy")]
    [InlineData(true, "--effective-date", "2026-01-01")]
    [InlineData(true, "--policy-only", "--table", "Sales")]
    public void Refresh_OnlyPartitionRiskyVariants_PreviewFirst(bool risky, params string[] args)
    {
        var session = new StubRefreshSession();
        var services = TestServices.Create();
        var root = TestRoot.With(new RefreshCommand(
            [new StubRefreshProvider(session)], services.State, services.LoadCurrentSession).Build());

        var (exitCode, stdout, _) = Invoke(
            root, ["refresh", "-s", RemoteEndpoint, "-d", "Sales", .. args, "--non-interactive", "--output-format", "json"]);

        Assert.Equal(risky ? PreviewGate.PreviewExitCode : 0, exitCode);
        Assert.Equal(!risky, session.RefreshCalled || session.PolicyApplied);
        Assert.NotEmpty(stdout);
    }

    // Deploy and refresh preview before they ask, so a target no provider can open fails in
    // the preview with the provider's error, never with a confirmation error.
    [Theory]
    [InlineData("deploy", "model.bim")]
    [InlineData("refresh", "-s", RemoteEndpoint, "-d", "Sales", "--refresh-type", "clearvalues")]
    [InlineData("refresh", "--policy-only", "--table", "Sales", "-s", RemoteEndpoint, "-d", "Sales")]
    public void PreviewFirstCommands_PreviewBeforeAsking(params string[] args)
    {
        var (exitCode, _, stderr) = Invoke([.. args, "--non-interactive", "--output-format", "json"]);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    // Only the persisting forms ask: without --save/--save-to/--stage/--revert a mutation is a
    // preview that stays in memory.
    [Theory]
    [InlineData("rm", "SomeTable")]
    [InlineData("replace", "foo", "bar")]
    [InlineData("bpa", "run", "--model", "SomeModel", "--fix", "--allow-delete")]
    public void Mutation_Preview_NeedsNoConfirmation(params string[] args)
    {
        var (exitCode, _, stderr) = Invoke([.. args, "--non-interactive", "--output-format", "json"]);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Pass --yes to confirm", stderr);
    }

    // mv additionally skips the gate for --stage, which defers it to 'stage commit'.
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

    private sealed class StubRefreshProvider(StubRefreshSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class StubRefreshSession : IModelSession, IModelRefreshSession, IRefreshPolicyApplySession, IRefreshPolicyMutationSession
    {
        public bool RefreshCalled { get; private set; }
        public bool PolicyApplied { get; private set; }
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelRefreshResult> RefreshAsync(
            ModelRefreshRequest request, IProgress<RefreshProgress>? progress, TextWriter? traceWriter, CancellationToken cancellationToken)
        {
            RefreshCalled = true;
            return Task.FromResult(new ModelRefreshResult(
                "stub-server", request.Database ?? "stub", request.RefreshType, DurationMs: 1,
                Tables: [new RefreshTableResult("Sales", 100, 5, 5, 10)],
                Totals: new RefreshTableResult("Total", 100, 5, 5, 10)));
        }

        public string GenerateRefreshScript(ModelRefreshRequest request)
            => "{\"refresh\":{\"type\":\"" + request.RefreshType + "\"}}";

        public RefreshPolicyInfo? GetRefreshPolicy(string table)
            => new(table, "Import", "Year", 10, "Day", 3, 0, "", "RangeStart RangeEnd", [], []);

        public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request) => throw new NotSupportedException();

        public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false) => throw new NotSupportedException();

        public Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(RefreshPolicyApplyRequest request, CancellationToken cancellationToken)
        {
            PolicyApplied = true;
            return Task.FromResult(new RefreshPolicyApplyResult(
                "server", "Sales", request.Table, request.EffectiveDate!.Value, request.Refresh, [], 1));
        }
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
