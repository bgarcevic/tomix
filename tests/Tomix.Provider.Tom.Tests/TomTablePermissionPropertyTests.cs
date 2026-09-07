using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Table-permission property coverage for set: the writable scalars (metadata permission, RLS
/// filter), value parsing errors, and read-back through the summarizer. Permission names are
/// derived by TOM from the referenced table, so no name case exists.
/// </summary>
public sealed class TomTablePermissionPropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, permission) = NewModel();

        mutator.SetProperty(Set("filterExpression", "FALSE()"));
        mutator.SetProperty(Set("metadataPermission", "None"));

        Assert.Equal("FALSE()", permission.FilterExpression);
        Assert.Equal(MetadataPermission.None, permission.MetadataPermission);
    }

    [Fact]
    public void SetProperty_MetadataPermission_RejectsUnknownEnumName()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("metadataPermission", "Bogus")));

        Assert.Contains("must be one of: Default, None, Read", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, permission) = NewModel();

        mutator.SetProperty(Set("metadataPermission", "None"));

        var snapshot = TomModelSummarizer.Snapshot((Database)permission.Table.Model.Database, "M");
        var permissionObject = snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Role)
            .Children.Single(c => c.Kind == ModelObjectKind.TablePermission);
        var projected = ModelPropertyCatalog.Project(permissionObject);

        // metadataPermission is the permission's Detail, so the descriptor reads it from there
        // (lowercase, as the summarizer renders permission details).
        Assert.Equal("none", projected["metadataPermission"]);
        Assert.Equal("TRUE()", projected["filterExpression"]);
    }

    [Fact]
    public void SetProperty_UnknownTablePermissionProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("metadataPermission", ex.Message);
        Assert.Contains("filterExpression", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("Readers/T", [new ModelPropertyAssignment(property, value)], ModelObjectKind.TablePermission);

    private static (TomModelMutator Mutator, TablePermission Permission) NewModel()
    {
        // MetadataPermission is compatibility-gated at 1400+; 1702 clears it.
        var db = NewDatabase(compatibilityLevel: 1702);
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        db.Model.Tables.Add(table);
        var role = new ModelRole { Name = "Readers" };
        var permission = new TablePermission { Name = "T", Table = table, FilterExpression = "TRUE()" };
        role.TablePermissions.Add(permission);
        db.Model.Roles.Add(role);
        return (new TomModelMutator(db), permission);
    }
}
