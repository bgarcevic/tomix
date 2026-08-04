using System.Text.Json;
using Tomix.App.Connect;
using Tomix.App.State;

namespace Tomix.App.Tests;

/// <summary>
/// A <c>--local</c> session names a Power BI Desktop instance by its discovered
/// <c>localhost:&lt;port&gt;</c> endpoint. If that endpoint is not persisted the session says
/// "local" without naming a target, <c>ActiveModelResolver</c> resolves it to nothing, and every
/// later command fails as though nothing was connected.
/// </summary>
public sealed class ConnectHandlerLocalInstanceTests : IDisposable
{
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var folder in _cleanup.Where(Directory.Exists))
            Directory.Delete(folder, recursive: true);
    }

    private static ConnectSetRequest LocalRequest(string? server, string? database = null)
        => new(server, database, Model: null, Auth: null, Local: true, Profile: null);

    /// <param name="stillServes">
    /// Stands in for the live-instance check so these tests need no listening socket. Defaults to
    /// "the cached name is still valid".
    /// </param>
    private static void WithStore(
        Action<CliStateStore, ConnectHandler> test,
        Func<string?, string?, bool>? stillServes = null)
    {
        var dir = Directory.CreateTempSubdirectory("tomix-connect-local-tests-").FullName;
        try
        {
            var store = new CliStateStore(dir);
            test(store, new ConnectHandler(store, stillServes ?? ((_, _) => true)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("localhost:61696")]
    [InlineData("127.0.0.1:61696")]
    public void Set_PersistsDiscoveredDesktopEndpoint(string endpoint)
    {
        WithStore((store, handler) =>
        {
            handler.Set(LocalRequest(endpoint, "Sales Model"));

            var session = store.LoadCurrentSession();

            Assert.NotNull(session);
            Assert.Equal(endpoint, session.Server);
            Assert.Equal("Sales Model", session.Database);
            Assert.True(session.Local);
        });
    }

    [Fact]
    public void Set_LocalWithoutEndpoint_LeavesServerUnset()
    {
        WithStore((store, handler) =>
        {
            handler.Set(LocalRequest(server: null));

            Assert.Null(store.LoadCurrentSession()!.Server);
        });
    }

    [Fact]
    public void Set_LocalWithRemoteEndpoint_DoesNotStoreItAsLocal()
    {
        // Only a loopback endpoint is a --local target; a workspace endpoint arriving here would
        // otherwise be persisted as though Desktop were serving it.
        WithStore((store, handler) =>
        {
            handler.Set(LocalRequest("powerbi://api.powerbi.com/v1.0/myorg/ws"));

            Assert.Null(store.LoadCurrentSession()!.Server);
        });
    }

    // --- Cached report name ------------------------------------------------------------------

    private string WritePortFile(string port)
    {
        var folder = Directory.CreateTempSubdirectory("tomix-portfile-tests-").FullName;
        var path = Path.Combine(folder, "msmdsrv.port.txt");
        File.WriteAllBytes(path, System.Text.Encoding.Unicode.GetBytes(port)); // UTF-16LE, no BOM
        _cleanup.Add(folder);
        return path;
    }

    [Fact]
    public void Show_KeepsReportName_WhilePortFileStillHoldsThatPort()
    {
        var portFile = WritePortFile("61696");

        WithStore((_, handler) =>
        {
            handler.Set(LocalRequest("localhost:61696") with
            {
                ReportName = "B4 - Bonustimer",
                ReportPortFile = portFile
            });

            Assert.Equal("B4 - Bonustimer", handler.Show().Data!.Connection!.ReportName);
        });
    }

    [Fact]
    public void Show_DropsReportName_WhenTheInstanceIsNoLongerServing()
    {
        // Covers both ways the cache goes bad: Desktop restarted onto a different report that
        // reused the port, or Desktop exited and left its port file behind.
        WithStore(
            (_, handler) =>
            {
                handler.Set(LocalRequest("localhost:61696") with
                {
                    ReportName = "B4 - Bonustimer",
                    ReportPortFile = WritePortFile("61696")
                });

                var shown = handler.Show().Data!.Connection!;
                Assert.Null(shown.ReportName);
                Assert.Null(shown.ReportPortFile);
                Assert.Equal("localhost:61696", shown.Server); // the connection itself is untouched
            },
            stillServes: (_, _) => false);
    }

    [Theory]
    [InlineData("61696", true, true)]
    // A port file that still holds the port but has no listener behind it is the stale file
    // msmdsrv leaves on shutdown — discovery skips those, so the label must drop too.
    [InlineData("61696", false, false)]
    [InlineData("50987", true, false)]  // Desktop restarted onto a different port
    [InlineData("", true, false)]       // port file truncated
    public void StillServes_RequiresBothAMatchingPortAndALiveListener(
        string portFileContent,
        bool listening,
        bool expected)
    {
        var portFile = WritePortFile(portFileContent);

        Assert.Equal(expected, PowerBiDesktopDiscovery.StillServes(
            portFile, "localhost:61696", PowerBiDesktopDiscovery.TryReadPort, _ => listening));
    }

    [Theory]
    [InlineData("localhost:61696", true)]
    [InlineData("127.0.0.1:61696", true)]
    [InlineData("localhost:50987", false)]
    [InlineData("localhost", false)]        // no port
    [InlineData("localhost:abc", false)]    // unparseable port
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StillServes_ComparesTheEndpointPortAgainstThePortFile(string? endpoint, bool expected)
    {
        var portFile = WritePortFile("61696");

        Assert.Equal(expected, PowerBiDesktopDiscovery.StillServes(
            portFile, endpoint, PowerBiDesktopDiscovery.TryReadPort, _ => true));
    }

    [Fact]
    public void StillServes_MissingPortFile_IsFalse()
        => Assert.False(PowerBiDesktopDiscovery.StillServes(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.txt"),
            "localhost:61696",
            PowerBiDesktopDiscovery.TryReadPort,
            _ => true));

    // --- The cache must not reach command output or the recents file ------------------------

    [Fact]
    public void ShowAndSetJson_OmitTheReportCache()
    {
        // The cache is an internal display optimization, and ReportPortFile is an absolute path
        // inside the user's profile — neither belongs in the public connection contract.
        WithStore((_, handler) =>
        {
            var set = handler.Set(LocalRequest("localhost:61696") with
            {
                ReportName = "B4 - Bonustimer",
                ReportPortFile = WritePortFile("61696")
            });

            foreach (var json in new[]
            {
                JsonSerializer.Serialize(set.Data),
                JsonSerializer.Serialize(handler.Show().Data)
            })
            {
                Assert.Contains("localhost:61696", json, StringComparison.Ordinal);
                Assert.DoesNotContain("eportName", json, StringComparison.Ordinal);
                Assert.DoesNotContain("eportPortFile", json, StringComparison.Ordinal);
                Assert.DoesNotContain("msmdsrv.port.txt", json, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void Recents_DoNotStoreTheReportCache()
    {
        // Desktop picks a new port on every start, so a cached name is worthless in recents — and
        // it would leak the port-file path into `connect --recent --output-format json`.
        WithStore((store, handler) =>
        {
            handler.Set(LocalRequest("localhost:61696") with
            {
                ReportName = "B4 - Bonustimer",
                ReportPortFile = WritePortFile("61696")
            });

            var recent = Assert.Single(store.LoadRecentConnections()).Connection;
            Assert.Equal("localhost:61696", recent.Server);
            Assert.Null(recent.ReportName);
            Assert.Null(recent.ReportPortFile);
        });
    }

    [Fact]
    public void Set_RecordsRecent_ForDesktopInstance()
    {
        WithStore((store, handler) =>
        {
            handler.Set(LocalRequest("localhost:61696", "Sales Model"));

            var recent = Assert.Single(store.LoadRecentConnections());
            Assert.Equal("localhost:61696", recent.Connection.Server);
            Assert.Equal("Sales Model", recent.Connection.Database);
        });
    }
}
