using Tomix.App.Stage;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ActiveModelResolverTests
{
    [Fact]
    public void ResolveReference_ReturnsExplicitModel_WhenProvided()
    {
        var store = new CliStateStore(Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}"));
        var resolver = new ActiveModelResolver(store);

        var result = resolver.ResolveReference("./my-model.tmdl");

        Assert.Equal("./my-model.tmdl", result.Value);
    }

    [Fact]
    public void ResolveReference_ReturnsWorkspacePath_WhenWorkspaceIsLocal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
                Database: "MyModel",
                Model: null,
                Auth: null,
                Local: false,
                Profile: null,
                Workspace: "./my-workspace",
                WorkspaceFormat: "tmdl",
                WorkspaceAuth: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(null);

            Assert.Equal("./my-workspace", result.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ReturnsRemoteEndpoint_WhenWorkspaceIsRemote()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
                Database: "MyModel",
                Model: null,
                Auth: null,
                Local: false,
                Profile: null,
                Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws2",
                WorkspaceFormat: null,
                WorkspaceAuth: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(null, "MyModel");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", result.Value);
            Assert.Equal("MyModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ReturnsSessionModel_WhenNoWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: null,
                Model: "./my-model.tmdl",
                Auth: null,
                Local: false,
                Profile: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(null);

            Assert.Equal(Path.GetFullPath("./my-model.tmdl"), result.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ReturnsEmpty_WhenNoSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(null);

            Assert.Equal("", result.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ReturnsServerEndpoint_WhenServerGivenAndNoSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: null,
                database: "MyModel",
                server: "powerbi://api.powerbi.com/v1.0/myorg/ws");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", result.Value);
            Assert.Equal("MyModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ServerOverridesSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: "powerbi://api.powerbi.com/v1.0/myorg/session-ws",
                Database: "SessionModel",
                Model: "./session.tmdl",
                Auth: null,
                Local: false,
                Profile: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: null,
                database: "ExplicitModel",
                server: "powerbi://api.powerbi.com/v1.0/myorg/explicit-ws");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/explicit-ws", result.Value);
            Assert.Equal("ExplicitModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ServerFallsBackToSessionDatabase_WhenDatabaseOmitted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: "SessionModel",
                Model: null,
                Auth: null,
                Local: false,
                Profile: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: null,
                database: null,
                server: "powerbi://api.powerbi.com/v1.0/myorg/ws");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", result.Value);
            Assert.Equal("SessionModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ExpandsBareWorkspaceName_ToXmlaEndpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: null,
                database: "Contoso_core",
                server: "MyWorkspace");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", result.Value);
            Assert.Equal("Contoso_core", result.Database);
            Assert.True(result.IsRemote);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("localhost:1234")]
    [InlineData("127.0.0.1:1234")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales%20Archive")]
    [InlineData("asazure://aspaaseastus2.asazure.windows.net/myserver")]
    public void ResolveReference_LeavesFormedEndpoints_Unchanged(string server)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: null,
                database: "MyModel",
                server: server);

            Assert.Equal(server, result.Value);
            Assert.True(result.IsRemote);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveReference_ExplicitModelWinsOverServer()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveReference(
                explicitModel: "powerbi://api.powerbi.com/v1.0/myorg/model-ws",
                database: "ExplicitModel",
                server: "powerbi://api.powerbi.com/v1.0/myorg/other-ws");

            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/model-ws", result.Value);
            Assert.Equal("ExplicitModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ReturnsPrimaryRemote_WhenWorkspaceIsLocal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
                Database: "MyModel",
                Model: null,
                Auth: null,
                Local: false,
                Profile: null,
                Workspace: "./my-workspace",
                WorkspaceFormat: "tmdl",
                WorkspaceAuth: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveSyncTarget();

            Assert.NotNull(result);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", result.Value);
            Assert.Equal("MyModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ReturnsWorkspaceRemote_WhenWorkspaceIsRemote()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: "MyModel",
                Model: "./my-model.tmdl",
                Auth: null,
                Local: true,
                Profile: null,
                Workspace: "powerbi://api.powerbi.com/v1.0/myorg/ws2",
                WorkspaceFormat: null,
                WorkspaceAuth: "auto"));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveSyncTarget();

            Assert.NotNull(result);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws2", result.Value);
            Assert.Equal("MyModel", result.Database);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ReturnsNull_WhenNoWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: null,
                Model: "./my-model.tmdl",
                Auth: null,
                Local: false,
                Profile: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveSyncTarget();

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ReturnsNull_WhenNoSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveSyncTarget();

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ReturnsNull_WhenWorkspaceIsLocalAndNoRemote()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            store.SaveCurrentSession(new CliConnectionState(
                Server: null,
                Database: null,
                Model: "./primary.tmdl",
                Auth: null,
                Local: true,
                Profile: null,
                Workspace: "./workspace",
                WorkspaceFormat: "tmdl",
                WorkspaceAuth: null));

            var resolver = new ActiveModelResolver(store);
            var result = resolver.ResolveSyncTarget();

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
