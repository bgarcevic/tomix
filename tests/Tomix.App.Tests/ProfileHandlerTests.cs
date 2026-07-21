using Tomix.App.Profile;
using Tomix.App.State;

namespace Tomix.App.Tests;

public sealed class ProfileHandlerTests
{
    private static ProfileSetRequest Request(
        string name = "dev",
        string? server = null,
        string? database = null,
        string? model = null,
        string? auth = null,
        bool fromActive = false)
        => new(name, server, database, model, auth,
            Description: null, AutoFormat: null, ValidateOnMutation: null,
            BpaOnMutation: null, BpaOnDeploy: null, VertipaqOnRefresh: null,
            Spinner: null, FromActive: fromActive);

    private static void WithStore(Action<CliStateStore> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            test(new CliStateStore(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Set_PersistsExplicitValues()
    {
        WithStore(store =>
        {
            var result = new ProfileHandler(store).Set(Request(server: "powerbi://api.powerbi.com/v1.0/myorg/ws", database: "Sales"));

            Assert.True(result.Success);
            var saved = store.LoadProfiles()["dev"];
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", saved.Server);
            Assert.Equal("Sales", saved.Database);
        });
    }

    [Fact]
    public void Set_RequiresName()
    {
        WithStore(store =>
        {
            var result = new ProfileHandler(store).Set(Request(name: " "));

            Assert.False(result.Success);
            Assert.Equal("TOMIX_PROFILE_NAME_REQUIRED", result.Diagnostics.Single().Code);
        });
    }

    [Fact]
    public void Set_FromActive_CopiesActiveConnection()
    {
        WithStore(store =>
        {
            store.SaveCurrentSession(new CliConnectionState(
                "powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", Model: null, "interactive", Local: false, Profile: null));

            var result = new ProfileHandler(store).Set(Request(fromActive: true));

            Assert.True(result.Success);
            var saved = store.LoadProfiles()["dev"];
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", saved.Server);
            Assert.Equal("Sales", saved.Database);
            Assert.Equal("interactive", saved.Auth);
        });
    }

    [Fact]
    public void Set_FromActive_ExplicitValuesWin()
    {
        WithStore(store =>
        {
            store.SaveCurrentSession(new CliConnectionState(
                "powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", Model: null, Auth: null, Local: false, Profile: null));

            var result = new ProfileHandler(store).Set(Request(database: "Finance", fromActive: true));

            Assert.True(result.Success);
            var saved = store.LoadProfiles()["dev"];
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", saved.Server);
            Assert.Equal("Finance", saved.Database);
        });
    }

    [Fact]
    public void Set_FromActive_PreservesWorkspaceState_AndConnectRestoresIt()
    {
        WithStore(store =>
        {
            store.SaveCurrentSession(new CliConnectionState(
                Server: null, Database: null, Model: "/models/sales", Auth: null, Local: true, Profile: null,
                Workspace: "/workspace/sales", WorkspaceFormat: "tmdl", WorkspaceAuth: "interactive"));

            var result = new ProfileHandler(store).Set(Request(fromActive: true));

            Assert.True(result.Success);
            var saved = store.LoadProfiles()["dev"];
            Assert.Equal("/workspace/sales", saved.Workspace);
            Assert.Equal("tmdl", saved.WorkspaceFormat);
            Assert.Equal("interactive", saved.WorkspaceAuth);

            var connect = new Connect.ConnectHandler(store).Set(new Connect.ConnectSetRequest(
                Server: null, Database: null, Model: null, Auth: null, Local: false, Profile: "dev",
                Workspace: null, WorkspaceFormat: null, WorkspaceAuth: null));

            Assert.True(connect.Success);
            var session = store.LoadCurrentSession();
            Assert.Equal("/workspace/sales", session!.Workspace);
            Assert.Equal("tmdl", session.WorkspaceFormat);
            Assert.Equal("interactive", session.WorkspaceAuth);
        });
    }

    [Fact]
    public void Set_FromActive_FailsWithoutActiveConnection()
    {
        WithStore(store =>
        {
            var result = new ProfileHandler(store).Set(Request(fromActive: true));

            Assert.False(result.Success);
            Assert.Equal("TOMIX_NO_ACTIVE_CONNECTION", result.Diagnostics.Single().Code);
            Assert.Equal(2, result.ExitCode);
        });
    }

    [Fact]
    public void Remove_DeletesExistingProfile()
    {
        WithStore(store =>
        {
            var handler = new ProfileHandler(store);
            handler.Set(Request(server: "powerbi://api.powerbi.com/v1.0/myorg/ws"));

            var result = handler.Remove("dev");

            Assert.True(result.Success);
            Assert.True(result.Data!.Removed);
            Assert.False(store.LoadProfiles().ContainsKey("dev"));
        });
    }

    [Fact]
    public void Remove_ReportsMissingProfile()
    {
        WithStore(store =>
        {
            var result = new ProfileHandler(store).Remove("nope");

            Assert.True(result.Success);
            Assert.False(result.Data!.Removed);
        });
    }
}
