using System.CommandLine;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Cli.Interactive;
using Tomix.Cli.Serve;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Tests.Support;
using Tomix.Ui;

namespace Tomix.Cli.Tests;

/// <summary>
/// One live session shared by several clients over the localhost endpoint (#369): an edit by one
/// client reaches the others, a transaction holds other clients' edits until it ends, and the
/// endpoint refuses requests without the token or from another host or origin. The server is an
/// in-memory test server, so nothing listens on the network.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class SharedSessionTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Fact]
    public async Task AnEditByOneClient_ReachesTheOthers()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await using var agent = await shared.ConnectAsync("agent");

        var added = await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/Margin", ["type"] = "Measure", ["expression"] = "1" });

        Assert.NotNull(added["result"]);
        var changed = await web.NotificationAsync("model.changed");
        Assert.Equal("agent-1", (string?)changed["origin"]!["client"]);
        Assert.Contains(changed["changes"]!.AsArray(), change => (string?)change!["path"] == "Sales/Margin");
    }

    [Fact]
    public async Task ClientIds_AreNumberedPerName()
    {
        await using var shared = await Shared.StartAsync();
        await using var first = await shared.ConnectAsync("web");
        await using var second = await shared.ConnectAsync("web");

        Assert.Equal("web-1", first.ClientId);
        Assert.Equal("web-2", second.ClientId);
        Assert.Equal(["web-1", "web-2"], (await shared.StatusAsync())["clients"]!.AsArray().Select(id => (string?)id));
    }

    [Fact]
    public async Task AnOpenTransaction_HoldsOtherClientsEdits_UntilItCommits()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await using var agent = await shared.ConnectAsync("agent");

        await web.RequestAsync("transaction.begin", new JsonObject { ["label"] = "review" });
        await web.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" });
        var held = agent.SendAsync("object.add", new JsonObject { ["path"] = "Sales/B", ["type"] = "Measure", ["expression"] = "2" });
        var status = await shared.StatusAsync();
        await web.RequestAsync("transaction.commit");
        var answered = await agent.AnswerAsync(await held);

        Assert.Equal("web-1", (string?)status["transaction"]!["client"]);
        Assert.Equal("review", (string?)status["transaction"]!["label"]);
        Assert.NotNull(answered["result"]);
        var batches = await agent.NotificationsAsync("model.changed", count: 2);
        Assert.Equal(["web-1", "agent-1"], batches.Select(batch => (string?)batch["origin"]!["client"]));
        Assert.True((long)batches[1]["version"]! > (long)batches[0]["version"]!);
    }

    [Fact]
    public async Task AClientThatLeaves_HasItsTransactionRolledBack_AndTheOthersKeepTheSession()
    {
        await using var shared = await Shared.StartAsync();
        await using var agent = await shared.ConnectAsync("agent");
        await using (var web = await shared.ConnectAsync("web"))
        {
            await web.RequestAsync("transaction.begin");
            await web.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" });
        }

        var closed = await agent.NotificationAsync("transaction.closed");
        var get = await agent.RequestAsync("object.get", new JsonObject { ["path"] = "Sales/A" });

        Assert.Equal("rolledBack", (string?)closed["outcome"]);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)get["error"]!["data"]!["code"]);
        Assert.Equal(["agent-1"], (await shared.StatusAsync())["clients"]!.AsArray().Select(id => (string?)id));
    }

    [Theory]
    [InlineData("session.open")]
    [InlineData("session.close")]
    public async Task OpeningOrClosing_WhileOthersAreConnected_IsRefused(string method)
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await using var agent = await shared.ConnectAsync("agent");

        var answer = await agent.RequestAsync(method, method == "session.open"
            ? new JsonObject { ["model"] = shared.ModelPath }
            : new JsonObject { ["discard"] = true });

        Assert.Equal("TOMIX_SESSION_IN_USE", (string?)answer["error"]!["data"]!["code"]);
        Assert.NotNull((await web.RequestAsync("session.status"))["result"]);
    }

    [Fact]
    public async Task Status_ReportsTheSession_ItsClients_AndTheLastChange()
    {
        await using var shared = await Shared.StartAsync();
        var before = await shared.StatusAsync();
        await using var agent = await shared.ConnectAsync("agent");
        await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" });
        var after = await shared.StatusAsync();

        Assert.Equal(
            ["protocolVersion", "model", "state", "dirty", "version", "undoSteps", "redoSteps", "canReload", "sourceUnavailable", "transaction", "clients", "lastChange"],
            after.Select(pair => pair.Key));
        Assert.Equal("clean", (string?)before["state"]);
        Assert.Null(before["lastChange"]);
        Assert.Equal("dirty", (string?)after["state"]);
        Assert.Equal(true, (bool?)after["dirty"]);
        Assert.Equal(1, (int?)after["undoSteps"]);
        Assert.Equal(true, (bool?)after["canReload"]);
        Assert.Equal(false, (bool?)after["sourceUnavailable"]);
        Assert.Null(after["transaction"]);
        Assert.Equal("agent-1", (string?)after["lastChange"]!["client"]);
        Assert.Equal((long?)after["version"], (long?)after["lastChange"]!["version"]);
    }

    [Fact]
    public async Task Status_FollowsTheServerGoingAway_ThoughNoSessionStateChanges()
    {
        var model = SampleModel.CopyToTemp();
        try
        {
            var services = TestServices.Create();
            var opener = new SessionOpener(Providers, services.State, services.Staging);
            var (opened, failure) = await opener.TryOpenAsync(opener.Resolve(model.Path, null, null), showSpinner: false, CancellationToken.None);
            Assert.True(opened is not null, failure?.Message);
            await using var session = new UnreachableSession(opened);
            var host = new SessionHost((_, _) => new RootCommand(), opener, TextWriter.Null, session);

            Assert.Equal(false, (bool?)JsonNode.Parse(host.Status)!["sourceUnavailable"]);
            session.SourceUnavailable = true;
            Assert.Equal(true, (bool?)JsonNode.Parse(host.Status)!["sourceUnavailable"]);
            session.SourceUnavailable = false;
            Assert.Equal(false, (bool?)JsonNode.Parse(host.Status)!["sourceUnavailable"]);
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>A real session whose <see cref="ILiveModelSession.SourceUnavailable"/> the test sets.</summary>
    private sealed class UnreachableSession(ILiveModelSession inner) : ILiveModelSession
    {
        public bool SourceUnavailable { get; set; }
        public ModelReference Reference => inner.Reference;
        public SessionState State => inner.State;
        public bool IsDirty => inner.IsDirty;
        public long Version => inner.Version;
        public bool CanUndo => inner.CanUndo;
        public bool CanRedo => inner.CanRedo;
        public IReadOnlyList<LiveHistoryStep> History => inner.History;
        public bool CanReload => inner.CanReload;
        public string SourcePath => inner.SourcePath;

        public event EventHandler<ModelChangeBatch>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public event EventHandler<SessionStateChange>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public Task<LiveModelSnapshot> GetLiveSnapshotAsync(CancellationToken cancellationToken) => inner.GetLiveSnapshotAsync(cancellationToken);
        public Task<ILiveSessionLease> LeaseAsync(LiveLeaseOptions options, CancellationToken cancellationToken) => inner.LeaseAsync(options, cancellationToken);
        public Task<ILiveSessionLease> BeginTransactionAsync(LiveLeaseOptions options, CancellationToken cancellationToken) => inner.BeginTransactionAsync(options, cancellationToken);
        public Task<ModelChangeBatch?> UndoAsync(string? client, CancellationToken cancellationToken) => inner.UndoAsync(client, cancellationToken);
        public Task<ModelChangeBatch?> RedoAsync(string? client, CancellationToken cancellationToken) => inner.RedoAsync(client, cancellationToken);
        public Task<bool> CheckSourceAsync(CancellationToken cancellationToken) => inner.CheckSourceAsync(cancellationToken);
        public Task<ModelChangeBatch> ReloadAsync(string? client, CancellationToken cancellationToken) => inner.ReloadAsync(client, cancellationToken);
        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken) => inner.GetSummaryAsync(cancellationToken);
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => inner.GetSnapshotAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("Bearer wrong", null, HttpStatusCode.Unauthorized)]
    [InlineData("Bearer {token}", null, HttpStatusCode.OK)]
    [InlineData(null, "?token={token}", HttpStatusCode.OK)]
    public async Task Status_NeedsTheToken(string? authorization, string? query, HttpStatusCode expected)
    {
        await using var shared = await Shared.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status" + query?.Replace("{token}", shared.Token, StringComparison.Ordinal));
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization.Replace("{token}", shared.Token, StringComparison.Ordinal));

        using var response = await shared.Http.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Unauthorized)
            Assert.Contains("TOMIX_UI_UNAUTHORIZED", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Host", "evil.example")]
    [InlineData("Host", "localhost:8080")]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Origin", "null")]
    public async Task Requests_NamingAnotherHostOrOrigin_AreRefused(string header, string value)
    {
        await using var shared = await Shared.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {shared.Token}");
        if (header == "Host")
            request.Headers.Host = value;
        else
            request.Headers.TryAddWithoutValidation(header, value);

        using var response = await shared.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("TOMIX_UI_FORBIDDEN", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requests_FromTheHostsOwnOrigin_AreAccepted()
    {
        await using var shared = await Shared.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {shared.Token}");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");

        using var response = await shared.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AWebSocket_WithoutTheToken_IsRefused()
    {
        await using var shared = await Shared.StartAsync();
        var client = shared.Server.CreateWebSocketClient();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));

        Assert.Contains("401", refused.Message);
    }

    [Fact]
    public async Task ThePage_IsServed_WithItsScriptUnderAFreshNonce_AndNoReferrer()
    {
        await using var shared = await Shared.StartAsync();

        using var response = await shared.Http.GetAsync($"/?token={shared.Token}");
        using var again = await shared.Http.GetAsync($"/?token={shared.Token}");
        using var refused = await shared.Http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = System.Text.RegularExpressions.Regex.Match(policy, "script-src 'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"<script nonce=\"{nonce}\">", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(nonce, again.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task TheServeRelay_MakesAStdioClient_OneMoreClientOfTheSharedSession()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");

        var relay = await shared.RelayAsync(
            ServeCommandTests.Initialize(),
            ServeCommandTests.Request(2, "object.add", new JsonObject { ["path"] = "Sales/Relayed", ["type"] = "Measure", ["expression"] = "1" }),
            ServeCommandTests.Request(3, "shutdown"),
            ServeCommandTests.Exit());

        Assert.Equal(0, relay.ExitCode);
        Assert.Equal("test-1", (string?)relay.Result(1)["clientId"]);
        Assert.Equal("Sales/Relayed", (string?)relay.Result(2)["data"]!["added"]);
        Assert.Single(relay.Notifications("model.changed"));
        var changed = await web.NotificationAsync("model.changed");
        Assert.Equal("test-1", (string?)changed["origin"]!["client"]);
    }

    [Fact]
    public async Task TheServeRelay_ExitsWithOne_OnExitWithoutShutdown()
    {
        await using var shared = await Shared.StartAsync();

        var relay = await shared.RelayAsync(ServeCommandTests.Initialize(), ServeCommandTests.Exit());

        Assert.Equal(1, relay.ExitCode);
    }

    [Fact]
    public async Task WhenTheRelaysInputEnds_ItsAnswersStillArrive_AndTheSessionStaysOpen()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");

        var relay = await shared.RelayAsync(
            ServeCommandTests.Initialize(),
            ServeCommandTests.Request(2, "object.add", new JsonObject { ["path"] = "Sales/Kept", ["type"] = "Measure", ["expression"] = "1" }));

        Assert.Equal(0, relay.ExitCode);
        Assert.Equal("Sales/Kept", (string?)relay.Result(2)["data"]!["added"]);
        var status = await shared.StatusAsync();
        Assert.Equal("dirty", (string?)status["state"]);
        Assert.Equal(["web-1"], status["clients"]!.AsArray().Select(id => (string?)id));
    }

    [Fact]
    public async Task ACommandOnTheLiveModel_RunsInTheSession_AsAnUndoStep_AndLeavesTheFilesAlone()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        var onDisk = SnapshotFiles(shared.ModelPath);

        var run = await shared.RouteAsync("add", "Sales/Routed", "--type", "Measure", "--expression", "1");

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Contains("Sales/Routed", run.Stdout);
        var changed = await web.NotificationAsync("model.changed");
        Assert.Equal("tx add-1", (string?)changed["origin"]!["client"]);
        var status = await shared.StatusAsync();
        Assert.Equal("dirty", (string?)status["state"]);
        Assert.Equal(1, (int?)status["undoSteps"]);
        Assert.Equal(["web-1"], status["clients"]!.AsArray().Select(id => (string?)id));
        Assert.Equal(onDisk, SnapshotFiles(shared.ModelPath));
    }

    [Fact]
    public async Task ACommandOnTheLiveModel_SeesTheSessionsUnsavedEdits()
    {
        await using var shared = await Shared.StartAsync();
        await using var agent = await shared.ConnectAsync("agent");
        await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/Unsaved", ["type"] = "Measure", ["expression"] = "1" });

        var run = await shared.RouteAsync("get", "Sales/Unsaved", "--output-format", "json");

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Contains("\"Sales/Unsaved\"", run.Stdout);
    }

    [Fact]
    public async Task ARoutedCommand_KeepsItsExitCodeAndError()
    {
        await using var shared = await Shared.StartAsync();

        var run = await shared.RouteAsync("get", "Sales/Missing", "--error-format", "json");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)JsonNode.Parse(run.Stderr)!["code"]);
    }

    [Fact]
    public async Task ARoutedSave_WritesTheSession_AndTellsTheOtherClients()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await shared.RouteAsync("add", "Sales/SavedByRoute", "--type", "Measure", "--expression", "1");

        var run = await shared.RouteAsync("save");

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal(Path.GetFullPath(shared.ModelPath), (string?)(await web.NotificationAsync("session.saved"))["savedTo"]);
        Assert.Equal("clean", (string?)(await shared.StatusAsync())["state"]);
        Assert.Contains(
            Directory.EnumerateFiles(shared.ModelPath, "*.tmdl", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("SavedByRoute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADashValue_SendsTheCommandsStdin()
    {
        await using var shared = await Shared.StartAsync();
        await using var agent = await shared.ConnectAsync("agent");
        InputValueResolver.Stdin.Value = new StringReader("6 * 7\n");

        var run = await shared.RouteAsync("add", "Sales/Piped", "--type", "Measure", "--expression", "-");

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("6 * 7", run.Routed!.Stdin);
        var get = await agent.RequestAsync("object.get", new JsonObject { ["path"] = "Sales/Piped" });
        Assert.Contains("6 * 7", get["result"]!["data"]!.ToJsonString());
    }

    [Fact]
    public async Task ARoutedCommand_NamesTheModelByItsFullPath()
    {
        await using var shared = await Shared.StartAsync();
        var full = Path.GetFullPath(shared.ModelPath);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, full);

        var named = shared.Plan("summary", relative)!;
        var unnamed = shared.Plan("summary")!;

        Assert.Equal(["summary", full], named.Args);
        Assert.Equal(["--model", full, "summary"], unnamed.Args);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("bpa", "rules", "list")]
    [InlineData("summary", "--recent", "1")]
    public void CommandsTheSessionDoesNotRun_RunOnTheirOwn(params string[] args)
    {
        using var model = SampleModel.CopyToTemp();
        using var live = new TempDir();
        var services = TestServices.Create();
        var registry = new LiveRegistry(live.Path, _ => true);
        using var _ = registry.Register(new LiveEntry(Path.GetFullPath(model.Path), Environment.ProcessId, 80, "token", DateTimeOffset.UtcNow));

        var routed = new LiveCommandRoute(registry, services.State).Plan(FullRoot(services).Parse([.. args, model.Path]));

        Assert.Null(routed);
    }

    [Fact]
    public void AModelNoSessionHolds_RunsOnItsOwn()
    {
        using var model = SampleModel.CopyToTemp();
        using var live = new TempDir();
        var services = TestServices.Create();

        var routed = new LiveCommandRoute(new LiveRegistry(live.Path, _ => true), services.State)
            .Plan(FullRoot(services).Parse(["set", "Sales/A", model.Path, "--set", "Description=x"]));

        Assert.Null(routed);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("undo")]
    public async Task CommandRun_RunsOnlyTheRoutedCommands(string command)
    {
        await using var shared = await Shared.StartAsync();
        await using var agent = await shared.ConnectAsync("agent");

        var answer = await agent.RequestAsync("command.run", new JsonObject { ["args"] = new JsonArray(command, shared.ModelPath) });

        Assert.Equal(-32602, (int?)answer["error"]!["code"]);
        Assert.Contains("cannot run through command.run", (string?)answer["error"]!["message"]);
    }

    [Fact]
    public async Task FilesChangedOnDisk_TurnTheSessionStale_ForEveryClient_AndASaveIsRefused()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await using var agent = await shared.ConnectAsync("agent");
        await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/Mine", ["type"] = "Measure", ["expression"] = "1" });
        var theirs = EditSalesOnDisk(shared);

        var states = await web.NotificationsAsync("session.state", 2);
        var save = await agent.RequestAsync("session.save");

        Assert.Equal("stale", (string?)states[^1]["state"]);
        Assert.Equal("TOMIX_SESSION_STALE", (string?)save["error"]!["data"]!["code"]);
        Assert.Equal(theirs, File.ReadAllText(SalesFile(shared)));
    }

    [Fact]
    public async Task ASaveWithForce_KeepsTheSessionsVersion_OverTheFiles()
    {
        await using var shared = await Shared.StartAsync();
        await using var agent = await shared.ConnectAsync("agent");
        await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/Mine", ["type"] = "Measure", ["expression"] = "1" });
        EditSalesOnDisk(shared);

        var save = await agent.RequestAsync("session.save", new JsonObject { ["force"] = true });

        Assert.NotNull(save["result"]);
        var sales = File.ReadAllText(SalesFile(shared));
        Assert.Contains("measure Mine = 1", sales);
        Assert.DoesNotContain("Theirs", sales);
        Assert.Equal("clean", (string?)(await shared.StatusAsync())["state"]);
    }

    [Fact]
    public async Task Reload_TakesTheFiles_AfterTheClientAgreesToDiscardItsChanges()
    {
        await using var shared = await Shared.StartAsync();
        await using var web = await shared.ConnectAsync("web");
        await using var agent = await shared.ConnectAsync("agent");
        await agent.RequestAsync("object.add", new JsonObject { ["path"] = "Sales/Mine", ["type"] = "Measure", ["expression"] = "1" });
        EditSalesOnDisk(shared);

        var refused = await web.RequestAsync("session.reload");
        var reloaded = await web.RequestAsync("session.reload", new JsonObject { ["discard"] = true });

        Assert.Equal("TOMIX_SESSION_DIRTY", (string?)refused["error"]!["data"]!["code"]);
        Assert.NotNull(reloaded["result"]);
        var changed = (await agent.NotificationsAsync("model.changed", 2))[^1];
        Assert.Equal("reload", (string?)changed["origin"]!["kind"]);
        Assert.Contains(changed["changes"]!.AsArray(), change => (string?)change!["path"] == "Sales/Theirs" && (string?)change["change"] == "added");
        Assert.Contains(changed["changes"]!.AsArray(), change => (string?)change!["path"] == "Sales/Mine" && (string?)change["change"] == "removed");
        var status = await shared.StatusAsync();
        Assert.Equal("clean", (string?)status["state"]);
        Assert.Equal(0, (int?)status["undoSteps"]);
    }

    [Fact]
    public async Task ARoutedSaveWithForce_OverwritesTheFiles()
    {
        await using var shared = await Shared.StartAsync();
        await shared.RouteAsync("add", "Sales/Mine", "--type", "Measure", "--expression", "1");
        EditSalesOnDisk(shared);

        var refused = await shared.RouteAsync("save", "--error-format", "json");
        var forced = await shared.RouteAsync("save", "--force");

        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("TOMIX_SESSION_STALE", (string?)JsonNode.Parse(refused.Stderr)!["code"]);
        Assert.True(forced.ExitCode == 0, forced.Stderr);
        Assert.Contains("measure Mine = 1", File.ReadAllText(SalesFile(shared)));
    }

    private static string SalesFile(Shared shared) => Path.Combine(shared.ModelPath, "tables", "Sales.tmdl");

    /// <summary>Adds a measure to the Sales file, as another editor would; returns the file's new text.</summary>
    private static string EditSalesOnDisk(Shared shared)
    {
        var text = File.ReadAllText(SalesFile(shared)).Replace(
            "\tmeasure 'Total Sales' = SUM ( Sales[Amount] )",
            "\tmeasure 'Total Sales' = SUM ( Sales[Amount] )\n\n\tmeasure Theirs = 2");
        Assert.Contains("Theirs", text);
        File.WriteAllText(SalesFile(shared), text);
        return text;
    }

    private static Dictionary<string, string> SnapshotFiles(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllText);

    private static System.CommandLine.RootCommand FullRoot(Tomix.App.AppServices services)
        => Program.BuildRootCommand(Providers, new CompositeExpressionFormatterClient([]), TestRoot.Version, services);

    private sealed record RouteRun(int ExitCode, string Stdout, string Stderr, LiveCommandRoute.Routed? Routed);

    /// <summary>A session on a copy of the sample model, shared through an in-memory test server.</summary>
    private sealed class Shared : IAsyncDisposable
    {
        private readonly TempDir _model;
        private readonly IDisposable _routing;
        private readonly SessionHost _host;
        private readonly UiHost _endpoint;
        private readonly Tomix.App.AppServices _services;
        private readonly TempDir _live = new();
        private readonly LiveRegistry _registry;
        private readonly IDisposable _registration;

        private Shared(TempDir model, IDisposable routing, SessionHost host, UiHost endpoint, string token, Tomix.App.AppServices services)
        {
            // As after 'tx connect': a command that names no model addresses this one.
            _services = services;
            _services.State.SaveCurrentSession(new Tomix.App.State.CliConnectionState(
                Server: null, Database: null, Model: model.Path, Auth: null, Local: true, Profile: null));
            _registry = new LiveRegistry(_live.Path, _ => true);
            _registration = _registry.Register(new LiveEntry(Path.GetFullPath(model.Path), Environment.ProcessId, 80, token, DateTimeOffset.UtcNow));
            _model = model;
            _routing = routing;
            _host = host;
            _endpoint = endpoint;
            Token = token;
            Server = endpoint.App.GetTestServer();
            Http = Server.CreateClient();
        }

        public string Token { get; }

        public TestServer Server { get; }

        public HttpClient Http { get; }

        public string ModelPath => _model.Path;

        public static async Task<Shared> StartAsync()
        {
            var model = SampleModel.CopyToTemp();
            var log = TextWriter.Synchronized(new StringWriter());
            var routing = ConsoleRouting.Install(log);
            var services = TestServices.Create();
            var formatter = new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient()]);
            var opener = new SessionOpener(Providers, services.State, services.Staging);
            var (session, failure) = await opener.TryOpenAsync(opener.Resolve(model.Path, null, null), showSpinner: false, CancellationToken.None);
            Assert.True(session is not null, failure?.Message);
            var host = new SessionHost(
                (scope, commands) => Program.BuildSessionRootCommand(scope, commands, Providers, formatter, services, httpClient: null),
                opener,
                log,
                session);
            var token = UiHost.NewToken();
            var endpoint = await WebEndpoint.StartAsync(host, "test", port: 80, token, CancellationToken.None, web => web.UseTestServer());
            return new Shared(model, routing, host, endpoint, token, services);
        }

        public async Task<Client> ConnectAsync(string name)
        {
            var socket = await Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/ws?token={Token}"), CancellationToken.None);
            var client = new Client(socket);
            var initialized = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "0",
                ["clientInfo"] = new JsonObject { ["name"] = name }
            });
            client.ClientId = (string?)initialized["result"]!["clientId"];
            return client;
        }

        /// <summary>Runs <c>tx serve</c>'s relay into the session, with <paramref name="frames"/> as its stdin.</summary>
        public async Task<ServeRun> RelayAsync(params string[] frames)
        {
            var socket = await Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/ws?token={Token}"), CancellationToken.None);
            using (socket)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(frames)));
                using var output = new MemoryStream();
                var exitCode = await ServeRelay.RunAsync(new StreamChannel(input, output), new WebSocketChannel(socket), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                return new ServeRun(exitCode, "", "", ServeRun.ReadFrames(output.ToArray()));
            }
        }

        /// <summary>What <c>tx</c> would send the session for <paramref name="args"/>.</summary>
        public LiveCommandRoute.Routed? Plan(params string[] args)
            => Route().Plan(FullRoot(_services).Parse(args));

        /// <summary>Runs <paramref name="args"/> as <c>tx</c> would while this session holds the model.</summary>
        public async Task<RouteRun> RouteAsync(params string[] args)
        {
            var parseResult = FullRoot(_services).Parse(args);
            var routed = Route().Plan(parseResult);
            Assert.NotNull(routed);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await Route().RunAsync(routed, parseResult, stdout, stderr, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            return new RouteRun(exitCode, stdout.ToString(), stderr.ToString(), routed);
        }

        private LiveCommandRoute Route()
            => new(_registry, _services.State, (_, cancellationToken) => Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/ws?token={Token}"), cancellationToken));

        public async Task<JsonObject> StatusAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/status");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _endpoint.DisposeAsync();
            await _host.CloseAsync();
            _routing.Dispose();
            _registration.Dispose();
            _live.Dispose();
            _model.Dispose();
        }
    }

    /// <summary>One protocol client on a WebSocket: requests by id, notifications kept in order.</summary>
    private sealed class Client(WebSocket socket) : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
        private readonly List<JsonObject> _received = [];
        private int _nextId;

        public string? ClientId { get; set; }

        public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters = null)
            => await AnswerAsync(await SendAsync(method, parameters));

        /// <summary>Sends a request and returns its id without waiting for the answer.</summary>
        public async Task<int> SendAsync(string method, JsonObject? parameters = null)
        {
            var id = ++_nextId;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
                message["params"] = parameters;
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            return id;
        }

        public Task<JsonObject> AnswerAsync(int id)
            => WaitForAsync(message => (int?)message["id"] == id && !message.ContainsKey("method"));

        public async Task<JsonObject> NotificationAsync(string method)
            => (await WaitForAsync(message => (string?)message["method"] == method))["params"]!.AsObject();

        public async Task<IReadOnlyList<JsonObject>> NotificationsAsync(string method, int count)
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (Matching(method).Count < count)
                _received.Add(await ReceiveAsync(timeout.Token));
            return [.. Matching(method).Take(count).Select(message => message["params"]!.AsObject())];
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            socket.Dispose();
        }

        private List<JsonObject> Matching(string method) => [.. _received.Where(message => (string?)message["method"] == method)];

        private async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> match)
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (true)
            {
                if (_received.FirstOrDefault(match) is { } found)
                    return found;
                _received.Add(await ReceiveAsync(timeout.Token));
            }
        }

        private async Task<JsonObject> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);
            return JsonNode.Parse(message.ToArray())!.AsObject();
        }
    }
}
