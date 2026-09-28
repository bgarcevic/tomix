using System.Text.Json;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx connect &lt;server&gt; --list</c> is the non-interactive way to enumerate the models on a
/// workspace that hosts several: it must work without a TTY, emit parseable JSON, and never
/// touch the saved session.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ConnectRemoteListTests
{
    [Fact]
    public void Json_ListsModels_WithoutChangingTheSession()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new ConnectCommand(
            [new CatalogProvider("Sales", "Finance")], FakeWorkspaceCatalog.Empty, () => null, services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(
            root.Parse(["connect", "Workspace", "--list", "--output-format", "json", "--non-interactive"]));

        Assert.Equal(0, captured.ExitCode);
        using var json = JsonDocument.Parse(captured.Stdout);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/Workspace", data.GetProperty("server").GetString());
        Assert.Equal(
            ["Finance", "Sales"],
            data.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Null(services.State.LoadCurrentSession());
    }

    [Fact]
    public void ListingFailure_ExitsOne_WithJsonDiagnostic()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new ConnectCommand(
            [new CatalogProvider { Failure = new InvalidOperationException("no access") }],
            FakeWorkspaceCatalog.Empty, () => null, services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(
            root.Parse(["connect", "Workspace", "--list", "--error-format", "json"]));

        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("\"code\": \"TOMIX_REMOTE_LIST_FAILED\"", captured.Stderr);
    }

    private sealed class CatalogProvider(params string[] databases) : IModelProvider, IServerCatalog
    {
        private readonly FakeServerCatalog _catalog = new(databases);

        public Exception? Failure { get; init; }

        public bool CanOpen(ModelReference reference) => false;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool CanList(ModelReference endpoint) => endpoint.IsRemote;

        public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(ModelReference endpoint, CancellationToken cancellationToken)
            => Failure is null
                ? _catalog.ListDatabasesAsync(endpoint, cancellationToken)
                : Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(Failure);
    }
}
