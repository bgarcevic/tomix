using Tomix.App.Connect;
using Tomix.App.State;

namespace Tomix.App.Tests;

public sealed class ConnectHandlerRecentsTests
{
    private static ConnectSetRequest RemoteRequest(string server, string database)
        => new(server, database, Model: null, Auth: null, Local: false, Profile: null);

    private static void WithStore(Action<CliStateStore, ConnectHandler> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            test(store, new ConnectHandler(store));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Set_RecordsRecent_ForRemoteConnection()
    {
        WithStore((store, handler) =>
        {
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));

            var recents = store.LoadRecentConnections();

            Assert.Single(recents);
            Assert.Equal("Sales", recents[0].Connection.Database);
        });
    }

    [Fact]
    public void Set_RecordsRecent_ForLocalModel()
    {
        WithStore((store, handler) =>
        {
            var model = Path.Combine(Path.GetTempPath(), "sales-model");
            handler.Set(new ConnectSetRequest(null, null, model, null, Local: true, Profile: null));

            var recents = store.LoadRecentConnections();

            Assert.Single(recents);
            Assert.Equal(model, recents[0].Connection.Model);
        });
    }

    [Fact]
    public void Set_ViaProfile_RecordsResolvedFieldsWithProfileName()
    {
        WithStore((store, handler) =>
        {
            store.SaveProfiles(new Dictionary<string, CliProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["prod"] = new(
                    "prod", "powerbi://api.powerbi.com/v1.0/myorg/prod", "Sales",
                    Model: null, Auth: "spn", Description: null,
                    AutoFormat: null, ValidateOnMutation: null, BpaOnMutation: null,
                    BpaOnDeploy: null, VertipaqOnRefresh: null, Spinner: null)
            });

            handler.Set(new ConnectSetRequest(null, null, null, null, Local: false, Profile: "prod"));

            var recents = store.LoadRecentConnections();

            Assert.Single(recents);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/prod", recents[0].Connection.Server);
            Assert.Equal("Sales", recents[0].Connection.Database);
            Assert.Equal("prod", recents[0].Connection.Profile);
        });
    }

    [Fact]
    public void Set_SameTargetTwice_KeepsSingleEntry()
    {
        WithStore((store, handler) =>
        {
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Finance"));
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));

            var recents = store.LoadRecentConnections();

            Assert.Equal(2, recents.Count);
            Assert.Equal("Sales", recents[0].Connection.Database);
            Assert.Equal("Finance", recents[1].Connection.Database);
        });
    }

    [Fact]
    public void Clear_LeavesRecentsIntact()
    {
        WithStore((store, handler) =>
        {
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));

            handler.Clear();

            Assert.Null(store.LoadCurrentSession());
            Assert.Single(store.LoadRecentConnections());
        });
    }

    [Fact]
    public void Recents_ReturnsStoreContents_NewestFirst()
    {
        WithStore((store, handler) =>
        {
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "First"));
            handler.Set(RemoteRequest("powerbi://api.powerbi.com/v1.0/myorg/ws", "Second"));

            var result = handler.Recents();

            Assert.True(result.Success);
            Assert.Equal(2, result.Data!.Connections.Count);
            Assert.Equal("Second", result.Data.Connections[0].Connection.Database);
        });
    }
}
