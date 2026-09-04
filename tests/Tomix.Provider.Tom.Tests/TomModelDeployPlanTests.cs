using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The plan is what <c>tx deploy --dry-run</c> diffs, so it must describe the model the deploy
/// would actually leave on the target — merged exactly as <see cref="TmslDeployScriptBuilder"/>
/// merges it. These tests run the pure <c>BuildPlan</c> against in-memory TOM databases: no
/// server, no auth, and the same preservation matrix the script builder is held to.
/// </summary>
public sealed class TomModelDeployPlanTests
{
    private const string PolicySourceExpression =
        "let Source = Sql.Database(\"srv\", \"db\"), Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered";

    [Fact]
    public void BuildPlan_TargetMissing_PlansFullSource_AndReportsCreate()
    {
        var source = Fixture(partitions: ["Fact"]);

        var plan = TomModelDeployer.BuildPlan(source, existing: null, "Prod", Request());

        Assert.False(plan.TargetExists);
        Assert.Null(plan.Target);
        Assert.Equal("Prod", plan.Planned.Name);
        Assert.Equal(["Fact"], PartitionNames(plan.Planned, "Fact"));
    }

    /// <summary>
    /// The provider-level proof of the #128 fix: with preserve-by-default options the deploy
    /// keeps the target's incremental-refresh partitions, so the plan must show them too — a
    /// dry run built from the raw source would report them as removed.
    /// </summary>
    [Fact]
    public void BuildPlan_PreserveDefaults_PlannedKeepsTargetPolicyPartitions()
    {
        var source = Fixture(partitions: ["Fact"]);
        var target = Fixture(partitions: ["2023Q1", "2023Q2", "2024"], name: "Prod");

        var plan = TomModelDeployer.BuildPlan(source, target, "Prod", Request());

        Assert.True(plan.TargetExists);
        Assert.Equal(["2023Q1", "2023Q2", "2024"], PartitionNames(plan.Planned, "Fact"));
        Assert.Equal(PartitionNames(plan.Target!, "Fact"), PartitionNames(plan.Planned, "Fact"));
    }

    [Fact]
    public void BuildPlan_FullOptions_PlannedMatchesSourcePartitions()
    {
        var source = Fixture(partitions: ["Fact"]);
        var target = Fixture(partitions: ["2023Q1", "2023Q2", "2024"], name: "Prod");

        var plan = TomModelDeployer.BuildPlan(
            source, target, "Prod", Request(ModelDeployOptions.Full));

        Assert.Equal(["Fact"], PartitionNames(plan.Planned, "Fact"));
        Assert.Equal(["2023Q1", "2023Q2", "2024"], PartitionNames(plan.Target!, "Fact"));
    }

    /// <summary>
    /// Preserved data sources are copied from the target verbatim, so the planned model's data
    /// sources must be indistinguishable from the target's — otherwise every dry run against a
    /// credentialed target would report noise the deploy never causes. Compared as property bags
    /// because this project must not reference Tomix.App's diff.
    /// </summary>
    [Fact]
    public void BuildPlan_PreservedDataSources_DoNotDiffAgainstLiveTarget()
    {
        var source = Fixture(partitions: ["Fact"], connectionString: "Data Source=dev");
        var target = Fixture(
            partitions: ["2024"], name: "Prod", connectionString: "Data Source=prod;Password=secret");

        var plan = TomModelDeployer.BuildPlan(source, target, "Prod", Request());

        var planned = DataSources(plan.Planned);
        var live = DataSources(plan.Target!);
        Assert.Equal(live.Count, planned.Count);
        Assert.Equal(live.Select(Describe), planned.Select(Describe));
    }

    /// <summary>
    /// The plan always reads the target (it is one half of the diff), so a remote endpoint
    /// without a token must fail with the same authentication error as script generation
    /// rather than an opaque connection failure.
    /// </summary>
    [Fact]
    public async Task GeneratePlanAsync_RemoteWithoutToken_RequiresAuthentication()
    {
        var source = Fixture(partitions: ["Fact"]);
        var request = new ModelDeployRequest(
            "powerbi://api.powerbi.com/v1.0/myorg/W", "Prod", CreateOnly: false, Force: false);

        await Assert.ThrowsAsync<Tomix.Core.Authentication.AuthenticationRequiredException>(
            () => TomModelDeployer.GeneratePlanAsync(
                source, request, tokenProvider: null, CancellationToken.None));
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static ModelDeployRequest Request(ModelDeployOptions? options = null)
        => new("localhost:59962", "Prod", CreateOnly: false, Force: false, options);

    /// <summary>A single incremental-refresh table plus a data source, so one fixture exercises
    /// both the partition and the connection preservation paths.</summary>
    private static Database Fixture(
        string[] partitions,
        string name = "Source",
        string connectionString = "Data Source=dev")
    {
        var db = new Database
        {
            Name = name,
            ID = $"{name}-id",
            CompatibilityLevel = 1601,
            Model = new Model { Name = "Model" }
        };

        var fact = new Table { Name = "Fact" };
        fact.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Int64, SourceColumn = "Amount" });
        foreach (var partition in partitions)
        {
            fact.Partitions.Add(new Partition
            {
                Name = partition,
                Mode = ModeType.Import,
                Source = new MPartitionSource { Expression = PolicySourceExpression }
            });
        }

        fact.RefreshPolicy = new BasicRefreshPolicy
        {
            SourceExpression = PolicySourceExpression,
            RollingWindowGranularity = RefreshGranularityType.Year,
            RollingWindowPeriods = 5,
            IncrementalGranularity = RefreshGranularityType.Month,
            IncrementalPeriods = 3
        };

        db.Model.Tables.Add(fact);
        db.Model.DataSources.Add(new ProviderDataSource
        {
            Name = "Warehouse",
            ConnectionString = connectionString
        });

        return db;
    }

    private static List<string> PartitionNames(ModelSnapshot snapshot, string tableName)
        => snapshot.Objects
            .Single(o => o.Kind == ModelObjectKind.Table && o.Name == tableName)
            .Children.Where(c => c.Kind == ModelObjectKind.Partition)
            .Select(c => c.Name)
            .ToList();

    private static List<ModelObject> DataSources(ModelSnapshot snapshot)
        => snapshot.Objects.Where(o => o.Kind == ModelObjectKind.DataSource)
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

    private static string Describe(ModelObject obj)
        => string.Join(
            "|",
            obj.Name,
            obj.Path,
            obj.Detail ?? "",
            obj.Description ?? "",
            string.Join(",", (obj.Properties ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}")));
}
