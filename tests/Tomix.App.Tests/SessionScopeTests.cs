using Tomix.App.State;
using Tomix.Platform.Configuration;

namespace Tomix.App.Tests;

/// <summary>
/// With no named session, the active connection is bound to the enclosing git repository or
/// worktree (else the working directory), so opening a new repo or worktree never silently
/// inherits a connection made somewhere else.
/// </summary>
public sealed class SessionScopeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tomix-scope-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(params string[] parts)
        => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static CliConnectionState Model(string path)
        => new(Server: null, Database: null, path, Auth: null, Local: true, Profile: null);

    [Fact]
    public void FindRoot_ReturnsTheEnclosingRepository()
    {
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, SessionScope.FindRoot(Dir("repo", "src", "deep")));
    }

    [Fact]
    public void FindRoot_TreatsALinkedWorktreeAsItsOwnRoot()
    {
        // A linked worktree has a `.git` *file*; it must not resolve to the main checkout.
        var main = Dir("main");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        var worktree = Dir("main", ".claude", "worktrees", "feature");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../../../.git/worktrees/feature");

        Assert.Equal(worktree, SessionScope.FindRoot(Path.Combine(worktree)));
        Assert.Equal(main, SessionScope.FindRoot(main));
    }

    [Fact]
    public void FindRoot_OutsideARepository_IsTheDirectoryItself()
    {
        var plain = Dir("plain", "folder");

        Assert.Equal(plain, SessionScope.FindRoot(plain));
    }

    [Fact]
    public void SessionIdFor_IsStableReadableAndDistinguishesSameNamedFolders()
    {
        var a = Dir("a", "sales");
        var b = Dir("b", "sales");

        Assert.Equal(SessionScope.SessionIdFor(a), SessionScope.SessionIdFor(a + Path.DirectorySeparatorChar));
        Assert.StartsWith("dir-sales-", SessionScope.SessionIdFor(a), StringComparison.Ordinal);
        Assert.NotEqual(SessionScope.SessionIdFor(a), SessionScope.SessionIdFor(b));
    }

    [Fact]
    public void SessionIdFor_IgnoresCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Dir("Sales");
        Assert.Equal(SessionScope.SessionIdFor(dir), SessionScope.SessionIdFor(dir.ToLowerInvariant()));
    }

    [Fact]
    public void Store_ConnectionDoesNotLeakIntoAnotherWorktree()
    {
        var config = Dir("config");
        var first = Dir("first");
        var second = Dir("second");
        Directory.CreateDirectory(Path.Combine(first, ".git"));
        File.WriteAllText(Path.Combine(second, ".git"), "gitdir: elsewhere");

        new CliStateStore(config, workingDirectory: first).SaveCurrentSession(Model("/models/sales"));

        Assert.Null(new CliStateStore(config, workingDirectory: second).LoadCurrentSession());
        Assert.Equal("/models/sales",
            new CliStateStore(config, workingDirectory: Dir("first", "nested")).LoadCurrentSession()!.Model);
    }

    [Fact]
    public void Store_DirectorySession_ReportsKindAndScope()
    {
        var repo = Dir("scoped");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var store = new CliStateStore(Dir("config"), workingDirectory: repo);

        Assert.Equal("directory", store.CurrentSessionKind);
        Assert.Equal(repo, store.CurrentSessionScope);
    }

    [Fact]
    public void Store_ExplicitSessionId_IsNotDirectoryScoped()
    {
        var config = Dir("config");
        var store = new CliStateStore(config, currentSessionId: "shared", workingDirectory: Dir("one"));
        store.SaveCurrentSession(Model("/models/sales"));

        Assert.Null(store.CurrentSessionScope);
        Assert.Equal("named", store.CurrentSessionKind);
        Assert.NotNull(new CliStateStore(config, currentSessionId: "shared", workingDirectory: Dir("two")).LoadCurrentSession());
    }
}
