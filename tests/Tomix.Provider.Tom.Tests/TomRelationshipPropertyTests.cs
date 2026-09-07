using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Relationship property coverage for set: the writable scalars (active flag, cross-filtering
/// and security-filtering behavior, both cardinalities, referential-integrity reliance, and
/// the datetime join behavior), value parsing errors, and read-back through the summarizer.
/// </summary>
public sealed class TomRelationshipPropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, relationship) = NewModel();

        mutator.SetProperty(Set("isActive", "false"));
        mutator.SetProperty(Set("crossFilteringBehavior", "BothDirections"));
        mutator.SetProperty(Set("securityFilteringBehavior", "BothDirections"));
        mutator.SetProperty(Set("relyOnReferentialIntegrity", "true"));
        mutator.SetProperty(Set("joinOnDateBehavior", "DatePartOnly"));

        Assert.False(relationship.IsActive);
        Assert.Equal(CrossFilteringBehavior.BothDirections, relationship.CrossFilteringBehavior);
        Assert.Equal(SecurityFilteringBehavior.BothDirections, relationship.SecurityFilteringBehavior);
        Assert.True(relationship.RelyOnReferentialIntegrity);
        Assert.Equal(DateTimeRelationshipBehavior.DatePartOnly, relationship.JoinOnDateBehavior);
    }

    [Fact]
    public void SetProperty_Cardinality_FlipsBothEnds()
    {
        var (mutator, relationship) = NewModel();

        mutator.SetProperty(Set("fromCardinality", "One"));
        mutator.SetProperty(Set("toCardinality", "Many"));

        Assert.Equal(RelationshipEndCardinality.One, relationship.FromCardinality);
        Assert.Equal(RelationshipEndCardinality.Many, relationship.ToCardinality);
    }

    [Fact]
    public void SetProperty_EnumsAndBool_RejectUnknownNames()
    {
        var (mutator, _) = NewModel();

        var security = Assert.Throws<ArgumentException>(() =>
            mutator.SetProperty(Set("securityFilteringBehavior", "Bogus")));
        Assert.Contains("must be one of: OneDirection, BothDirections, None", security.Message);

        var join = Assert.Throws<ArgumentException>(() =>
            mutator.SetProperty(Set("joinOnDateBehavior", "Bogus")));
        Assert.Contains("must be one of: DateAndTime, DatePartOnly", join.Message);

        var active = Assert.Throws<ArgumentException>(() =>
            mutator.SetProperty(Set("isActive", "maybe")));
        Assert.Contains("must be true or false", active.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, relationship) = NewModel();

        mutator.SetProperty(Set("isActive", "false"));
        mutator.SetProperty(Set("securityFilteringBehavior", "BothDirections"));
        mutator.SetProperty(Set("relyOnReferentialIntegrity", "true"));
        mutator.SetProperty(Set("joinOnDateBehavior", "DatePartOnly"));

        var snapshot = TomModelSummarizer.Snapshot((Database)relationship.Model.Database, "M");
        var relationshipObject = snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Relationship);
        var projected = ModelPropertyCatalog.Project(relationshipObject);

        Assert.Equal("BothDirections", projected["securityFilteringBehavior"]);
        Assert.Equal(true, projected["relyOnReferentialIntegrity"]);
        Assert.Equal("DatePartOnly", projected["joinOnDateBehavior"]);
        Assert.Equal(false, projected["isActive"]);
        // The active state is also folded into the detail line diff compares.
        Assert.Contains("inactive", relationshipObject.Detail);
    }

    [Fact]
    public void SetProperty_UnknownRelationshipProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("securityFilteringBehavior", ex.Message);
        Assert.Contains("relyOnReferentialIntegrity", ex.Message);
        Assert.Contains("joinOnDateBehavior", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("Sales[D]->Customers[D]", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Relationship);

    private static (TomModelMutator Mutator, SingleColumnRelationship Relationship) NewModel()
    {
        // SecurityFilteringBehavior.None (1561+) is the only compatibility gate in this
        // property set; 1702 clears it.
        var db = NewDatabase(compatibilityLevel: 1702);
        var sales = new Table { Name = "Sales" };
        sales.Partitions.Add(new Partition
        {
            Name = "Sales",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        sales.Columns.Add(new DataColumn { Name = "D", DataType = DataType.DateTime });
        var customers = new Table { Name = "Customers" };
        customers.Partitions.Add(new Partition
        {
            Name = "Customers",
            Source = new MPartitionSource { Expression = "let x = 2 in x" }
        });
        customers.Columns.Add(new DataColumn { Name = "D", DataType = DataType.DateTime });
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(customers);
        var relationship = new SingleColumnRelationship
        {
            FromColumn = sales.Columns["D"],
            ToColumn = customers.Columns["D"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        };
        db.Model.Relationships.Add(relationship);
        return (new TomModelMutator(db), relationship);
    }
}
