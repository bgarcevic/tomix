using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Where a save landed and which model it addressed (issue #161): the persistence boundary and
/// the target a mutation result reports.
/// </summary>
public sealed class MutationPersistenceTests
{
    [Theory]
    [InlineData("C:/model/def", null, "C:/model/def", "C:/model/def", PersistenceKind.File)]
    [InlineData("localhost:51234", null, "0f1e", "localhost:51234 / 0f1e", PersistenceKind.LiveModel)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/ws", null, "Sales", "powerbi://api.powerbi.com/v1.0/myorg/ws / Sales", PersistenceKind.Service)]
    [InlineData("localhost:51234", "C:/copy", "C:/copy", "C:/copy", PersistenceKind.File)]
    public void Describe_ReportsWhereTheSaveLanded(
        string model, string? saveTo, string savedPath, string expectedSavedTo, PersistenceKind expected)
    {
        var (savedTo, persistence) = MutationLifecycle.Describe(new ModelReference(model), saveTo, savedPath);

        Assert.Equal(expectedSavedTo, savedTo);
        Assert.Equal(expected, persistence);
    }

    [Fact]
    public void For_LocalPath_HasNoTarget()
        => Assert.Null(MutationTarget.For(new ModelReference("C:/model/def"), connection: null));

    [Fact]
    public void For_DesktopSession_UsesTheCachedReportName()
    {
        var connection = new CliConnectionState(
            "localhost:51234", Database: null, Model: null, Auth: null, Local: true, Profile: null,
            ReportName: "Revenue Opportunities");

        var target = MutationTarget.For(new ModelReference("localhost:51234"), connection);

        Assert.Equal(new MutationTarget("localhost:51234", null, "Revenue Opportunities"), target);
    }

    [Fact]
    public void Merge_FillsTheDatabaseFromTheSave_AndKeepsTheFriendlyName()
    {
        // `tx connect --local` names no database; the save reports the one it wrote to.
        var fromConnection = new MutationTarget("localhost:51234", null, "Revenue Opportunities");
        var fromSave = new MutationTarget("localhost:51234", "646944d4", null);

        var merged = MutationTarget.Merge(fromConnection, fromSave);

        Assert.Equal(new MutationTarget("localhost:51234", "646944d4", "Revenue Opportunities"), merged);
    }

    [Fact]
    public void Merge_WithoutSave_KeepsTheConnectionTarget()
    {
        var fromConnection = new MutationTarget("localhost:51234", null, "Revenue Opportunities");

        Assert.Same(fromConnection, MutationTarget.Merge(fromConnection, fromSave: null));
    }

    [Fact]
    public void Unchanged_DryRun_StaysADryRun()
    {
        var outcome = MutationOutcome.Unchanged with { DryRunRequested = true };

        Assert.Equal(MutationStatus.Unchanged, outcome.Status);
        Assert.True(outcome.DryRunRequested);
        Assert.True(MutationOutcome.DryRun.DryRunRequested);
        Assert.False(MutationOutcome.Preview.DryRunRequested);
    }
}
