using Tomix.App.Connect;
using Tomix.App.State;

namespace Tomix.App.Tests;

/// <summary>
/// The session side of <see cref="ConnectHandler"/>: which session file <c>connect</c> reports,
/// <c>--clear</c> / <c>--clear --all</c>, and the stale-session sweep that runs on every connect.
/// </summary>
public sealed class ConnectHandlerSessionTests
{
    private static readonly ConnectSetRequest RemoteRequest =
        new("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", Model: null, Auth: null, Local: false, Profile: null);

    private static CliConnectionState LocalModel(string model)
        => new(Server: null, Database: null, model, Auth: null, Local: true, Profile: null);

    private static string AddSessionFile(CliStateStore store, string sessionId, string json = "{}")
    {
        Directory.CreateDirectory(store.SessionsDirectory);
        var path = Path.Combine(store.SessionsDirectory, $"{sessionId}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Saves a directory session scoped to <paramref name="folder"/> and returns its file.</summary>
    private static string AddDirectorySession(TempConfigDir config, string folder)
    {
        var other = new CliStateStore(config.Path, workingDirectory: folder);
        other.SaveCurrentSession(LocalModel("/models/other"));
        return other.CurrentSessionFile;
    }

    [Fact]
    public void Show_ReportsCurrentSession_WithAndWithoutConnection()
    {
        using var config = new TempConfigDir();
        var handler = new ConnectHandler(config.State);

        var empty = handler.Show().Data!;
        Assert.False(empty.Active);
        Assert.Equal(config.State.CurrentSessionId, empty.Session!.Id);
        Assert.Equal(config.State.CurrentSessionKind, empty.Session.Kind);
        Assert.Equal(config.State.CurrentSessionScope, empty.Session.Scope);
        Assert.Equal(config.State.CurrentSessionFile, empty.Session.Path);

        config.State.SaveCurrentSession(LocalModel("/models/sales"));
        Assert.Equal(config.State.CurrentSessionFile, handler.Show().Data!.Session!.Path);
    }

    [Fact]
    public void SaveCurrentSession_RecordsScope_ButPublicProjectionsDropIt()
    {
        using var config = new TempConfigDir();
        var handler = new ConnectHandler(config.State);

        var set = handler.Set(RemoteRequest).Data!;

        Assert.Equal(config.State.CurrentSessionScope, config.State.LoadCurrentSession()!.Scope);
        Assert.Null(set.PublicConnection.Scope);
        Assert.Null(handler.Show().Data!.PublicConnection!.Scope);
        Assert.Null(Assert.Single(config.State.LoadRecentConnections()).Connection.Scope);
    }

    [Fact]
    public void Clear_WithActiveSession_DeletesFileAndReportsCleared()
    {
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(LocalModel("/models/sales"));
        var other = AddSessionFile(config.State, "other");

        var result = new ConnectHandler(config.State).Clear().Data!;

        Assert.True(result.Cleared);
        Assert.Null(result.Removed);
        Assert.False(File.Exists(config.State.CurrentSessionFile));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Clear_WithoutActiveSession_ReportsNotCleared()
    {
        using var config = new TempConfigDir();

        Assert.False(new ConnectHandler(config.State).Clear().Data!.Cleared);
    }

    [Fact]
    public void ClearAll_DeletesEverySessionFile_IncludingCurrent()
    {
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(LocalModel("/models/sales"));
        AddSessionFile(config.State, "named");
        AddSessionFile(config.State, $"pid-{Environment.ProcessId}");

        var result = new ConnectHandler(config.State).Clear(all: true).Data!;

        Assert.True(result.Cleared);
        Assert.Equal(3, result.Removed);
        Assert.Empty(config.State.ListSessions());
    }

    [Fact]
    public void SelectStaleSessions_FindsDeletedFoldersAndDeadPids_Only()
    {
        using var config = new TempConfigDir();
        config.State.SaveCurrentSession(LocalModel("/models/sales"));
        var gone = config.CreateSubdirectory("deleted-worktree");
        var orphaned = AddDirectorySession(config, gone);
        Directory.Delete(gone);
        var alive = AddDirectorySession(config, config.CreateSubdirectory("live-worktree"));
        var deadPid = AddSessionFile(config.State, $"pid-{int.MaxValue}");
        AddSessionFile(config.State, $"pid-{Environment.ProcessId}");
        AddSessionFile(config.State, "pid-not-a-number");
        AddSessionFile(config.State, "named");
        AddSessionFile(config.State, "legacy-directory-without-scope", """{"model":"/m","local":true}""");
        AddSessionFile(config.State, "corrupt", "{not json");

        var stale = config.State.SelectStaleSessions().Select(s => s.Path).Order().ToList();

        Assert.Equal(new[] { deadPid, orphaned }.Order(), stale);
        Assert.DoesNotContain(alive, stale);
    }

    [Fact]
    public void Set_PrunesStaleSessions_AndKeepsTheRest()
    {
        using var config = new TempConfigDir();
        var gone = config.CreateSubdirectory("deleted-worktree");
        var orphaned = AddDirectorySession(config, gone);
        Directory.Delete(gone);
        var named = AddSessionFile(config.State, "named");

        new ConnectHandler(config.State).Set(RemoteRequest);

        Assert.False(File.Exists(orphaned));
        Assert.True(File.Exists(named));
        Assert.True(File.Exists(config.State.CurrentSessionFile));
    }
}
