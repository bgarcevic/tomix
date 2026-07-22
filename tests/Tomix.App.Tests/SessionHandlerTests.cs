using Tomix.App.Session;
using Tomix.App.State;

namespace Tomix.App.Tests;

public sealed class SessionHandlerTests
{
    private static void WithStore(Action<CliStateStore, SessionHandler> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tomix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CliStateStore(dir);
            test(store, new SessionHandler(store));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static CliConnectionState LocalModel(string model)
        => new(Server: null, Database: null, model, Auth: null, Local: true, Profile: null);

    private static string AddSessionFile(CliStateStore store, string sessionId)
    {
        Directory.CreateDirectory(store.SessionsDirectory);
        var path = Path.Combine(store.SessionsDirectory, $"{sessionId}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void Show_WithoutActiveSession_ReportsMetadataAndNotExists()
    {
        WithStore((store, handler) =>
        {
            var result = handler.Show();

            Assert.True(result.Success);
            Assert.Equal(store.CurrentSessionId, result.Data!.SessionId);
            Assert.Equal(store.CurrentSessionKind, result.Data.Kind);
            Assert.Equal(store.CurrentSessionFile, result.Data.Path);
            Assert.False(result.Data.Exists);
            Assert.Null(result.Data.Active);
        });
    }

    [Fact]
    public void Show_WithActiveSession_ReturnsState()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));

            var result = handler.Show();

            Assert.True(result.Success);
            Assert.True(result.Data!.Exists);
            Assert.Equal("/models/sales", result.Data.Active!.Model);
        });
    }

    [Fact]
    public void Show_CorruptSessionFile_ReportsNoActiveState()
    {
        WithStore((store, handler) =>
        {
            Directory.CreateDirectory(store.SessionsDirectory);
            File.WriteAllText(store.CurrentSessionFile, "{not json");

            var result = handler.Show();

            Assert.True(result.Success);
            Assert.False(result.Data!.Exists);
            Assert.Null(result.Data.Active);
        });
    }

    [Fact]
    public void List_WithoutSessionsDirectory_ReturnsEmpty()
    {
        WithStore((_, handler) =>
        {
            var result = handler.List();

            Assert.True(result.Success);
            Assert.Empty(result.Data!.Sessions);
        });
    }

    [Fact]
    public void List_MarksCurrentSession_AndSortsById()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            AddSessionFile(store, "zzz-other");
            AddSessionFile(store, "aaa-other");

            var sessions = handler.List().Data!.Sessions;

            Assert.Equal(3, sessions.Count);
            Assert.Equal(
                sessions.Select(s => s.SessionId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
                sessions.Select(s => s.SessionId));
            Assert.Equal(store.CurrentSessionFile, Assert.Single(sessions, s => s.Current).Path);
        });
    }

    [Fact]
    public void Clear_WithActiveSession_DeletesFileAndReportsCleared()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));

            var result = handler.Clear();

            Assert.True(result.Success);
            Assert.True(result.Data!.Cleared);
            Assert.False(File.Exists(store.CurrentSessionFile));
            Assert.Null(store.LoadCurrentSession());
        });
    }

    [Fact]
    public void Clear_WithoutActiveSession_ReportsNotCleared()
    {
        WithStore((_, handler) =>
        {
            var result = handler.Clear();

            Assert.True(result.Success);
            Assert.False(result.Data!.Cleared);
        });
    }

    [Fact]
    public void Clear_LeavesOtherSessionsAlone()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            var other = AddSessionFile(store, "other");

            handler.Clear();

            Assert.True(File.Exists(other));
        });
    }

    [Fact]
    public void Prune_WithoutSessionsDirectory_RemovesNothing()
    {
        WithStore((_, handler) =>
        {
            var result = handler.Prune(all: false, dryRun: false);

            Assert.True(result.Success);
            Assert.Equal(0, result.Data!.Removed);
            Assert.False(result.Data.DryRun);
        });
    }

    [Fact]
    public void Prune_RemovesOnlyDeadPidSessions_ByDefault()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            var named = AddSessionFile(store, "named");
            var livePid = AddSessionFile(store, $"pid-{Environment.ProcessId}");
            var deadPid = AddSessionFile(store, $"pid-{int.MaxValue}");

            var result = handler.Prune(all: false, dryRun: false);

            Assert.Equal(1, result.Data!.Removed);
            Assert.False(File.Exists(deadPid));
            Assert.True(File.Exists(named));
            Assert.True(File.Exists(livePid));
            Assert.True(File.Exists(store.CurrentSessionFile));
        });
    }

    [Fact]
    public void Prune_PreservesMalformedPidSessions()
    {
        WithStore((store, handler) =>
        {
            var nonNumeric = AddSessionFile(store, "pid-not-a-number");
            var missing = AddSessionFile(store, "pid-");
            var negative = AddSessionFile(store, "pid--1");

            var result = handler.Prune(all: false, dryRun: false);

            Assert.Equal(0, result.Data!.Removed);
            Assert.True(File.Exists(nonNumeric));
            Assert.True(File.Exists(missing));
            Assert.True(File.Exists(negative));
        });
    }

    [Fact]
    public void Prune_All_RemovesEverythingExceptCurrent()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            AddSessionFile(store, "named");
            AddSessionFile(store, $"pid-{Environment.ProcessId}");
            AddSessionFile(store, $"pid-{int.MaxValue}");

            var result = handler.Prune(all: true, dryRun: false);

            Assert.Equal(3, result.Data!.Removed);
            Assert.True(File.Exists(store.CurrentSessionFile));
            Assert.Equal(store.CurrentSessionFile, Assert.Single(store.ListSessions()).Path);
        });
    }

    [Fact]
    public void Prune_DryRun_CountsWhatDefaultPruneWouldRemove_WithoutDeleting()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            AddSessionFile(store, "named");
            AddSessionFile(store, $"pid-{Environment.ProcessId}");
            AddSessionFile(store, $"pid-{int.MaxValue}");

            var result = handler.Prune(all: false, dryRun: true);

            Assert.Equal(1, result.Data!.Removed);
            Assert.True(result.Data.DryRun);
            Assert.Equal(4, store.ListSessions().Count);
        });
    }

    [Fact]
    public void Prune_DryRunAll_CountsAllNonCurrent_WithoutDeleting()
    {
        WithStore((store, handler) =>
        {
            store.SaveCurrentSession(LocalModel("/models/sales"));
            AddSessionFile(store, "named");
            AddSessionFile(store, $"pid-{Environment.ProcessId}");

            var result = handler.Prune(all: true, dryRun: true);

            Assert.Equal(2, result.Data!.Removed);
            Assert.True(result.Data.DryRun);
            Assert.Equal(3, store.ListSessions().Count);
        });
    }
}
