using Tomix.App.State;

namespace Tomix.App.Tests;

public sealed class CliStateStoreRecentsTests
{
    private static CliConnectionState Remote(string server, string? database, string? auth = null)
        => new(server, database, Model: null, auth, Local: false, Profile: null);

    private static CliConnectionState LocalModel(string model)
        => new(Server: null, Database: null, model, Auth: null, Local: true, Profile: null);

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
    public void Add_InsertsNewestFirst_AndStampsLastUsed()
    {
        WithStore(store =>
        {
            var before = DateTimeOffset.UtcNow;
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "First"));
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Second"));

            var recents = store.LoadRecentConnections();

            Assert.Equal(2, recents.Count);
            Assert.Equal("Second", recents[0].Connection.Database);
            Assert.Equal("First", recents[1].Connection.Database);
            Assert.True(recents[0].LastUsed >= before);
        });
    }

    [Fact]
    public void Add_SameRemoteTarget_DifferentCasing_ReplacesAndPromotes()
    {
        WithStore(store =>
        {
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Other"));
            store.AddRecentConnection(Remote("POWERBI://API.POWERBI.COM/v1.0/myorg/ws", "SALES"));

            var recents = store.LoadRecentConnections();

            Assert.Equal(2, recents.Count);
            Assert.Equal("SALES", recents[0].Connection.Database);
            Assert.Equal("Other", recents[1].Connection.Database);
        });
    }

    [Fact]
    public void Add_SameModelPath_Dedups()
    {
        WithStore(store =>
        {
            store.AddRecentConnection(LocalModel("/models/sales"));
            store.AddRecentConnection(LocalModel("/models/sales"));

            Assert.Single(store.LoadRecentConnections());
        });
    }

    [Fact]
    public void Add_DistinctDatabase_IsDistinctEntry()
    {
        WithStore(store =>
        {
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales"));
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Finance"));

            Assert.Equal(2, store.LoadRecentConnections().Count);
        });
    }

    [Fact]
    public void Add_AuthChange_UpdatesEntryInPlace()
    {
        WithStore(store =>
        {
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", auth: "interactive"));
            store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", auth: "spn"));

            var recents = store.LoadRecentConnections();

            Assert.Single(recents);
            Assert.Equal("spn", recents[0].Connection.Auth);
        });
    }

    [Fact]
    public void Add_CapsAtMaxRecentConnections_DroppingOldest()
    {
        WithStore(store =>
        {
            for (var i = 1; i <= CliStateStore.MaxRecentConnections + 5; i++)
                store.AddRecentConnection(Remote("powerbi://api.powerbi.com/v1.0/myorg/ws", $"Model{i}"));

            var recents = store.LoadRecentConnections();

            Assert.Equal(CliStateStore.MaxRecentConnections, recents.Count);
            Assert.Equal($"Model{CliStateStore.MaxRecentConnections + 5}", recents[0].Connection.Database);
            Assert.DoesNotContain(recents, r => r.Connection.Database == "Model1");
        });
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
        => WithStore(store => Assert.Empty(store.LoadRecentConnections()));

    [Fact]
    public void Load_EmptyFile_ReturnsEmpty()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.RecentConnectionsFile, "");
            Assert.Empty(store.LoadRecentConnections());
        });
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty_WithoutThrowing()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.RecentConnectionsFile, "{not json");
            Assert.Empty(store.LoadRecentConnections());
        });
    }

    [Fact]
    public void Add_SkipsStateWithoutTarget()
    {
        WithStore(store =>
        {
            store.AddRecentConnection(new CliConnectionState(
                Server: null, Database: "Orphan", Model: null, Auth: null, Local: true, Profile: null));

            Assert.Empty(store.LoadRecentConnections());
        });
    }

    [Fact]
    public void RecentKey_ModelWins_OverServer()
    {
        var state = new CliConnectionState("server", "db", "/models/sales", null, Local: true, Profile: null);

        Assert.Equal("model:/models/sales", CliStateStore.RecentKey(state));
    }

    [Fact]
    public void RecentKey_Remote_CombinesServerAndDatabase()
        => Assert.Equal(
            "remote:srv\0db",
            CliStateStore.RecentKey(Remote("srv", "db")));
}
