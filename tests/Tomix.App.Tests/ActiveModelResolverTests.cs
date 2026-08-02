using Tomix.App.Stage;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ActiveModelResolverTests
{
    [Fact]
    public void ResolveSyncTarget_FromConnectionSnapshot_UsesSharedPolicy()
    {
        var connection = new CliConnectionState(
            Server: "powerbi://api.powerbi.com/v1.0/myorg/primary",
            Database: "Sales",
            Model: "/local/model",
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/mirror");

        var result = ActiveModelResolver.ResolveSyncTarget(connection);

        Assert.NotNull(result);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/mirror", result.Value);
        Assert.Equal("Sales", result.Database);
    }

    [Fact]
    public void ResolveSyncTarget_ForModel_ReturnsMirror_WhenModelIsTheSessionPrimary()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), "ModelB.SemanticModel");
        var connection = LocalWorkspaceConnection(modelPath);

        var result = ActiveModelResolver.ResolveSyncTarget(connection, new ModelReference(modelPath));

        Assert.NotNull(result);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/sandbox", result.Value);
        Assert.Equal("ModelB", result.Database);
    }

    [Fact]
    public void ResolveSyncTarget_ForModel_NormalizesPaths_BeforeComparing()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), "ModelB.SemanticModel");
        var connection = LocalWorkspaceConnection(modelPath);

        var unnormalized = Path.Combine(Path.GetTempPath(), ".", "ModelB.SemanticModel")
            + Path.DirectorySeparatorChar;
        var result = ActiveModelResolver.ResolveSyncTarget(connection, new ModelReference(unnormalized));

        Assert.NotNull(result);
    }

    [Fact]
    public void ResolveSyncTarget_ForModel_ResolvesSymlinks_BeforeComparing()
    {
        // An in-place save of the primary addressed through a link alias must keep syncing;
        // treating the alias as a different model would silently skip the mirror.
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        var modelPath = Path.Combine(dir, "ModelB.SemanticModel");
        Directory.CreateDirectory(modelPath);
        var linkPath = Path.Combine(dir, "alias");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, modelPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // Symlinks unavailable on this platform (e.g. Windows without developer mode).
            }

            var result = ActiveModelResolver.ResolveSyncTarget(
                LocalWorkspaceConnection(modelPath), new ModelReference(linkPath));

            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveSyncTarget_ForModel_ReturnsNull_WhenSourceIsAnExplicitRemote()
    {
        // Issue #134: a one-shot save of an explicit -s/-d source must not inherit the
        // session's mirror — deploying would replace the mirrored model with an unrelated one.
        var connection = LocalWorkspaceConnection(Path.Combine(Path.GetTempPath(), "ModelB.SemanticModel"));

        var result = ActiveModelResolver.ResolveSyncTarget(
            connection,
            new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/sandbox", "model-a"));

        Assert.Null(result);
    }

    [Fact]
    public void ResolveSyncTarget_ForModel_ReturnsNull_WhenSourceIsADifferentLocalPath()
    {
        var connection = LocalWorkspaceConnection(Path.Combine(Path.GetTempPath(), "ModelB.SemanticModel"));

        var result = ActiveModelResolver.ResolveSyncTarget(
            connection,
            new ModelReference(Path.Combine(Path.GetTempPath(), "ModelA.SemanticModel")));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/primary", "Sales", true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/primary", "Other", false)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/elsewhere", "Sales", false)]
    public void ResolveSyncTarget_ForModel_MatchesServerAndDatabase_WhenPrimaryIsRemote(
        string server, string database, bool expectSync)
    {
        var connection = new CliConnectionState(
            Server: "powerbi://api.powerbi.com/v1.0/myorg/primary",
            Database: "Sales",
            Model: null,
            Auth: null,
            Local: false,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/mirror");

        var result = ActiveModelResolver.ResolveSyncTarget(
            connection, new ModelReference(server, database));

        Assert.Equal(expectSync, result is not null);
    }

    private static CliConnectionState LocalWorkspaceConnection(string modelPath)
        => new(
            Server: null,
            Database: "ModelB",
            Model: modelPath,
            Auth: null,
            Local: true,
            Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/sandbox");

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
