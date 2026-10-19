using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The offline halves of <c>tx deploy --rollback-on-error</c> (ADR 0005): when to roll back,
/// and the merged model that is copied over the live target inside the transaction. The
/// transaction itself needs a server; spikes/deploy-transaction records the live evidence.
/// </summary>
public sealed class TomModelDeployRollbackTests
{
    public static TheoryData<RuleSeverity[], bool> Validations => new()
    {
        { [], false },
        { [RuleSeverity.Info], false },
        { [RuleSeverity.Warning, RuleSeverity.Info], false },
        { [RuleSeverity.Error], true },
        { [RuleSeverity.Warning, RuleSeverity.Error], true }
    };

    [Theory]
    [MemberData(nameof(Validations))]
    public void ShouldRollBack_OnlyOnErrors(RuleSeverity[] severities, bool expected)
    {
        var validation = new DeployValidation(
            [.. severities.Select(severity => new DeployedObjectIssue(severity, "Measure", "Fact[M]", "State", null))]);

        Assert.Equal(expected, TomModelDeployer.ShouldRollBack(validation));
    }

    /// <summary>A check that could not run cannot vouch for the model, so it rolls back too.</summary>
    [Fact]
    public void ShouldRollBack_WhenTheCheckDidNotRun()
        => Assert.True(TomModelDeployer.ShouldRollBack(validation: null));

    /// <summary>
    /// The transactional path applies the same merge as the createOrReplace script: the target's
    /// identity, its preserved partitions, and its connection string with credentials, which a
    /// direct deploy must carry so the target keeps working.
    /// </summary>
    [Fact]
    public void BuildMergedDatabase_PreserveDefaults_KeepsTheTargetsIdentityPartitionsAndConnection()
    {
        var source = Fixture("Source", ["Fact"], "SUM ( Fact[Amount] ) * 2", "Data Source=dev");
        var target = Fixture("Prod", ["2023", "2024"], "SUM ( Fact[Amount] )", "Data Source=prod;Password=secret");

        var merged = TomModelDeployer.BuildMergedDatabase(source, target, Request());

        Assert.Equal("Prod", merged.Name);
        Assert.Equal("Prod-id", merged.ID);
        Assert.Equal(["2023", "2024"], merged.Model.Tables["Fact"].Partitions.Select(p => p.Name));
        Assert.Equal("SUM ( Fact[Amount] ) * 2", merged.Model.Tables["Fact"].Measures["Total"].Expression);
        var dataSource = Assert.IsType<ProviderDataSource>(Assert.Single(merged.Model.DataSources));
        Assert.Contains("Password=secret", dataSource.ConnectionString);
    }

    [Fact]
    public void BuildMergedDatabase_FullOptions_TakesTheSourcePartitions()
    {
        var source = Fixture("Source", ["Fact"], "SUM ( Fact[Amount] )", "Data Source=dev");
        var target = Fixture("Prod", ["2023", "2024"], "SUM ( Fact[Amount] )", "Data Source=prod");

        var merged = TomModelDeployer.BuildMergedDatabase(source, target, Request(ModelDeployOptions.Full));

        Assert.Equal(["Fact"], merged.Model.Tables["Fact"].Partitions.Select(p => p.Name));
    }

    /// <summary>
    /// Copying the merged model over the target is what the transaction saves: the source's
    /// edits land and the preserved objects stay, so unchanged partitions keep their data.
    /// </summary>
    [Fact]
    public void MergedModel_CopiedOverTheTarget_ChangesOnlyTheSourceEdits()
    {
        var source = Fixture("Source", ["Fact"], "SUM ( Fact[Amount] ) * 2", "Data Source=dev");
        var target = Fixture("Prod", ["2023", "2024"], "SUM ( Fact[Amount] )", "Data Source=prod");
        var partitionsBefore = target.Model.Tables["Fact"].Partitions.ToList();

        TomModelDeployer.BuildMergedDatabase(source, target, Request()).Model.CopyTo(target.Model);

        Assert.Equal("SUM ( Fact[Amount] ) * 2", target.Model.Tables["Fact"].Measures["Total"].Expression);
        Assert.Equal(["2023", "2024"], target.Model.Tables["Fact"].Partitions.Select(p => p.Name));
        Assert.Equal("Prod", target.Name);
        Assert.Same(partitionsBefore[0], target.Model.Tables["Fact"].Partitions["2023"]);
    }

    [Fact]
    public void DeleteDatabaseScript_AddressesTheDatabaseByName()
    {
        var script = JsonNode.Parse(TomModelDeployer.DeleteDatabaseScript("Sales \"QA\""))!;

        Assert.Equal("Sales \"QA\"", script["delete"]!["object"]!["database"]!.GetValue<string>());
    }

    private static ModelDeployRequest Request(ModelDeployOptions? options = null)
        => new("localhost:59962", "Prod", CreateOnly: false, Force: false, options, RollbackOnError: true);

    private static Database Fixture(string name, string[] partitions, string measure, string connectionString)
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
        fact.Measures.Add(new Measure { Name = "Total", Expression = measure });
        foreach (var partition in partitions)
        {
            fact.Partitions.Add(new Partition
            {
                Name = partition,
                Mode = ModeType.Import,
                Source = new MPartitionSource { Expression = "let Source = Sql.Database(\"srv\", \"db\") in Source" }
            });
        }

        db.Model.Tables.Add(fact);
        db.Model.DataSources.Add(new ProviderDataSource { Name = "Warehouse", ConnectionString = connectionString });
        return db;
    }
}
