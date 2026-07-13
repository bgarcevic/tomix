using Tomix.App.Deps;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class DepsModelHandlerTests
{
    [Fact]
    public async Task DirectUpstream_ResolvesQualifiedAndLoneReferences()
    {
        // Net = "[Total] - SUM(Sales[Tax])" -> measure Total (lone) + column Tax (qualified).
        var result = await Run(Request("Sales/Net"));

        var upstream = Paths(result.Data!.Upstream);
        Assert.Equal(["Sales/Tax", "Sales/Total"], upstream.Order());
        Assert.All(result.Data.Upstream, d => Assert.Empty(d.Children));
    }

    [Fact]
    public async Task Deep_Upstream_FollowsTransitiveClosure()
    {
        var result = await Run(Request("Sales/Margin", deep: true));

        // Margin -> Net,Total ; Net -> Total,Tax ; Total -> Amount ; Tax -> Amount.
        var reached = Paths(result.Data!.Upstream).Distinct().Order();
        Assert.Equal(["Sales/Amount", "Sales/Net", "Sales/Tax", "Sales/Total"], reached);
    }

    [Fact]
    public async Task Deep_Downstream_IncludesRelationshipAndTransitiveDependents()
    {
        var result = await Run(Request("Sales/Amount", deep: true));

        var reached = Paths(result.Data!.Downstream).Distinct().ToList();
        Assert.Contains("Relationships/rel1", reached); // relationship edge on FromColumn
        Assert.Contains("Sales/Total", reached);
        Assert.Contains("Sales/Net", reached);          // transitive: Amount <- Total <- Net
        Assert.Contains("Sales/Margin", reached);
    }

    [Fact]
    public async Task Deep_BreaksCycles_WithoutInfiniteRecursion()
    {
        // A = "[B] + 1", B = "[A] + 1".
        var result = await Run(Request("Sales/A", deep: true));

        var b = Assert.Single(result.Data!.Upstream);
        Assert.Equal("Sales/B", b.Path);
        var a = Assert.Single(b.Children);
        Assert.Equal("Sales/A", a.Path);
        Assert.Empty(a.Children); // cycle back to root is a leaf
    }

    [Fact]
    public async Task Deep_HonorsMaxDepth()
    {
        var result = await Run(Request("Sales/Margin", deep: true, maxDepth: 1));

        Assert.All(result.Data!.Upstream, d => Assert.Empty(d.Children));
        Assert.Equal(["Sales/Net", "Sales/Total"], Paths(result.Data.Upstream).Order());
    }

    [Fact]
    public async Task Upstream_TracksQuotedTableReferences()
    {
        // RowCount = "COUNTROWS('Region')" -> upstream edge to the Region table itself.
        var result = await Run(Request("Sales/RowCount"));

        var dep = Assert.Single(result.Data!.Upstream);
        Assert.Equal("Region", dep.Path);
        Assert.Equal("Table", dep.Type);
    }

    [Fact]
    public async Task Upstream_DoesNotTrackUnquotedTableReferences()
    {
        // An unquoted bare word (COUNTROWS(Region)) could be a VAR name, so it is not tracked.
        var result = await Run(Request("Sales/RowCountBare"));

        Assert.Empty(result.Data!.Upstream);
    }

    [Fact]
    public async Task AmbiguousPath_ListsCandidatesAndHint()
    {
        // "Amount" matches Sales/Amount and Region/Amount.
        var result = await Run(Request("Amount"));

        Assert.False(result.Success);
        var diagnostic = result.Diagnostics[0];
        Assert.Equal("TOMIX_OBJECT_AMBIGUOUS", diagnostic.Code);
        Assert.Contains("Sales/Amount (Column)", diagnostic.Message);
        Assert.Contains("Region/Amount (Column)", diagnostic.Message);
        Assert.Contains("-t <type>", diagnostic.Hint);
    }

    [Fact]
    public async Task Upstream_CoversKpiTargetExpression()
    {
        // KpiMeasure has Expression "1" but KpiTargetExpression "[Total]".
        var result = await Run(Request("Sales/KpiMeasure"));

        var dep = Assert.Single(result.Data!.Upstream);
        Assert.Equal("Sales/Total", dep.Path);
    }

    [Fact]
    public async Task Unused_ListsUnreferencedMeasuresButNotUsedObjects()
    {
        var result = await Run(Request(path: null, unused: true));

        var unused = result.Data!.Unused!.Select(u => u.Path).ToList();
        Assert.Contains("Sales/Unused", unused);
        Assert.Contains("Sales/HiddenStuff", unused);
        Assert.DoesNotContain("Sales/Total", unused);   // referenced by Net/Margin
        Assert.DoesNotContain("Sales/Amount", unused);  // referenced by Total/Tax/relationship
        Assert.DoesNotContain("Region/RegionKey", unused); // used by relationship
    }

    [Fact]
    public async Task Unused_Hidden_FiltersToHiddenObjects()
    {
        var result = await Run(Request(path: null, unused: true, hiddenOnly: true));

        var only = Assert.Single(result.Data!.Unused!);
        Assert.Equal("Sales/HiddenStuff", only.Path);
    }

    [Fact]
    public async Task UpstreamOnly_SuppressesDownstream()
    {
        var result = await Run(Request("Sales/Total", upstreamOnly: true));

        Assert.Empty(result.Data!.Downstream);
        Assert.NotEmpty(result.Data.Upstream);
    }

    [Fact]
    public async Task ReturnsFail_WhenPathMissingAndNotUnused()
    {
        var result = await Run(Request(path: null));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DEPS_PATH_REQUIRED", result.Diagnostics[0].Code);
    }

    private static Task<Core.Results.TomixResult<DepsModelResult>> Run(DepsModelRequest request)
        => new DepsModelHandler([new StubModelProvider()]).HandleAsync(request, CancellationToken.None);

    private static DepsModelRequest Request(
        string? path,
        bool deep = false,
        bool unused = false,
        bool hiddenOnly = false,
        bool upstreamOnly = false,
        bool downstreamOnly = false,
        int maxDepth = 10)
        => new(new ModelReference("any"), path, Type: null, upstreamOnly, downstreamOnly, deep, unused, hiddenOnly, maxDepth);

    private static List<string> Paths(IEnumerable<DependencyObject> deps)
    {
        var paths = new List<string>();
        void Walk(IEnumerable<DependencyObject> nodes)
        {
            foreach (var node in nodes)
            {
                paths.Add(node.Path);
                Walk(node.Children);
            }
        }

        Walk(deps);
        return paths;
    }

    private static ModelObject Measure(string table, string name, string expression, bool hidden = false,
        IReadOnlyDictionary<string, string>? props = null)
        => new(name, ModelObjectKind.Measure, $"{table}/{name}", null, expression, null, hidden, null, [], props);

    private static ModelObject Column(string table, string name, string? expression = null,
        IReadOnlyDictionary<string, string>? props = null)
        => new(name, ModelObjectKind.Column, $"{table}/{name}", null, expression, null, false, null, [], props);

    private sealed class StubModelProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession());
    }

    private sealed class StubSession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 2, 6, 8, 1, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
        {
            var sales = new ModelObject(
                "Sales", ModelObjectKind.Table, "Sales", "regular", null, null, false, null,
                [
                    Column("Sales", "Amount"),
                    Column("Sales", "Tax", "Sales[Amount] * 0.1"),
                    Measure("Sales", "Total", "SUM(Sales[Amount])"),
                    Measure("Sales", "Net", "[Total] - SUM(Sales[Tax])"),
                    Measure("Sales", "Margin", "DIVIDE([Net], [Total])"),
                    Measure("Sales", "A", "[B] + 1"),
                    Measure("Sales", "B", "[A] + 1"),
                    Measure("Sales", "Unused", "1"),
                    Measure("Sales", "RowCount", "COUNTROWS('Region')"),
                    Measure("Sales", "RowCountBare", "COUNTROWS(Region)"),
                    Measure("Sales", "HiddenStuff", "2", hidden: true),
                    Measure("Sales", "KpiMeasure", "1", props: new Dictionary<string, string>
                    {
                        ["KpiTargetExpression"] = "[Total]"
                    }),
                ]);

            var region = new ModelObject(
                "Region", ModelObjectKind.Table, "Region", "regular", null, null, false, null,
                [Column("Region", "RegionKey"), Column("Region", "Amount")]);

            var relationship = new ModelObject(
                "rel1", ModelObjectKind.Relationship, "Relationships/rel1", null, null, null, false, null, [],
                new Dictionary<string, string>
                {
                    ["FromColumn"] = "Sales[Amount]",
                    ["ToColumn"] = "Region[RegionKey]"
                });

            return Task.FromResult(new ModelSnapshot("stub", 1601, [sales, region, relationship]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
