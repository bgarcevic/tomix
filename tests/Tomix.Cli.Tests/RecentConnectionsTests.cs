using System.CommandLine;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

public sealed class RecentConnectionsTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("20", 20)]
    public void TryParseRecentIndex_AcceptsNoValueAndPositiveIndexes(string? raw, int expected)
    {
        Assert.True(RecentConnections.TryParseRecentIndex(raw, out var index));
        Assert.Equal(expected, index);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public void TryParseRecentIndex_RejectsJunk(string raw)
        => Assert.False(RecentConnections.TryParseRecentIndex(raw, out _));

    [Fact]
    public void FormatRecentLabel_LocalModel_UsesPath()
    {
        var state = new CliConnectionState(null, null, "/models/sales", null, Local: true, Profile: null);

        Assert.Equal("/models/sales", RecentConnections.FormatRecentLabel(state));
    }

    [Fact]
    public void FormatRecentLabel_Remote_CombinesServerAndDatabase()
    {
        var state = new CliConnectionState("localhost:52123", "Sales", null, null, Local: false, Profile: null);

        Assert.Equal("localhost:52123 / Sales", RecentConnections.FormatRecentLabel(state));
    }

    [Fact]
    public void FormatRecentLabel_ServerOnly_OmitsSeparator()
    {
        var state = new CliConnectionState("localhost:52123", null, null, null, Local: false, Profile: null);

        Assert.Equal("localhost:52123", RecentConnections.FormatRecentLabel(state));
    }

    [Fact]
    public void FormatRecentLabel_AppendsProfileAndMirrorAnnotations()
    {
        var state = new CliConnectionState(
            "srv", "db", null, null, Local: false, Profile: "prod", Workspace: "./mirror");

        Assert.Equal("srv / db (profile: prod) (mirror: ./mirror)", RecentConnections.FormatRecentLabel(state));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(90, "1m ago")]
    [InlineData(3_600, "1h ago")]
    [InlineData(7_500, "2h ago")]
    [InlineData(90_000, "1d ago")]
    public void FormatRecentAge_Buckets(int ageSeconds, string expected)
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, RecentConnections.FormatRecentAge(now.AddSeconds(-ageSeconds), now));
    }

    private static ParseResult Parse(params string[] args)
    {
        IReadOnlyList<IModelProvider> providers = [];
        var services = TestServices.Create();
        var root = TestRoot.With(new ConnectCommand(providers, FakeWorkspaceCatalog.Empty, () => null, services.State).Build());
        root.Subcommands.Add(new LsCommand(providers, services.State).Build());
        return root.Parse(args);
    }

    [Theory]
    [InlineData("connect", "--recent")]
    [InlineData("connect", "--recent", "2")]
    [InlineData("connect", "--recents")]
    [InlineData("ls", "--recent", "1")]
    public void RecentOption_ParsesCleanly(params string[] args)
    {
        var result = Parse(args);

        Assert.Empty(result.Errors);
        Assert.True(GlobalOptions.RecentSpecified(result));
    }

    [Fact]
    public void RecentOption_AbsentByDefault()
        => Assert.False(GlobalOptions.RecentSpecified(Parse("connect")));

    // Regression: a server-only recent must resolve against the picked entry, not the active
    // session, so it never inherits the active connection's database (a `load --recent` on a
    // server-only entry must not open the active session's database on that server).
    [Fact]
    public void CreateResolver_ServerOnlyRecent_DoesNotInheritDatabase()
    {
        var entry = new CliConnectionState(
            "powerbi://api.powerbi.com/v1.0/myorg/ws", Database: null, Model: null,
            Auth: null, Local: false, Profile: null);
        var source = new RecentConnections.ModelSource(entry.Model, entry.Server, entry.Database, entry);

        var reference = RecentConnections.CreateResolver(source, TestServices.Create().State)
            .ResolveReference(source.Model, source.Database, source.Server);

        Assert.True(reference.IsRemote);
        Assert.Null(reference.Database);
    }

    // Regression: the sync target for a recent must be the mirror stored with that entry, not the
    // active session's mirror — otherwise `save --recent` could push to the wrong workspace.
    [Fact]
    public void CreateResolver_Recent_UsesEntryMirrorAsSyncTarget()
    {
        var entry = new CliConnectionState(
            Server: null, Database: "Sales", Model: "/models/sales",
            Auth: null, Local: true, Profile: null,
            Workspace: "powerbi://api.powerbi.com/v1.0/myorg/mirror");
        var source = new RecentConnections.ModelSource(entry.Model, entry.Server, entry.Database, entry);

        var syncTarget = RecentConnections.CreateResolver(source, TestServices.Create().State).ResolveSyncTarget();

        Assert.NotNull(syncTarget);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/mirror", syncTarget!.Value);
        Assert.Equal("Sales", syncTarget.Database);
    }

    // The complementary branch: with no recent entry the resolver falls back to the active
    // session in the store, so the sync target comes from the session rather than the source.
    [Fact]
    public void CreateResolver_NonRecentSource_ReadsActiveSession()
    {
        var services = TestServices.Create();
        try
        {
            services.State.SaveCurrentSession(new CliConnectionState(
                Server: null, Database: "Active", Model: "/models/active",
                Auth: null, Local: true, Profile: null,
                Workspace: "powerbi://api.powerbi.com/v1.0/myorg/active-mirror"));

            var source = new RecentConnections.ModelSource("/models/x", null, null, RecentEntry: null);

            var syncTarget = RecentConnections.CreateResolver(source, services.State).ResolveSyncTarget();

            Assert.NotNull(syncTarget);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/active-mirror", syncTarget!.Value);
            Assert.Equal("Active", syncTarget.Database);
        }
        finally
        {
            if (Directory.Exists(services.ConfigDirectory))
                Directory.Delete(services.ConfigDirectory, recursive: true);
        }
    }
}
