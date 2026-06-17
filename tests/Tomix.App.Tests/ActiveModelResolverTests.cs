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
