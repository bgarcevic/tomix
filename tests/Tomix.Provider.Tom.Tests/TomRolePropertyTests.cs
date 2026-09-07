using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Role property coverage for set: the writable scalars (name, description, model permission),
/// value parsing errors, and read-back through the summarizer.
/// </summary>
public sealed class TomRolePropertyTests
{
    [Fact]
    public void SetProperty_ScalarProperties_Apply()
    {
        var (mutator, role) = NewModel();

        // The rename goes last: it changes the path the other assignments resolve through.
        mutator.SetProperty(Set("description", "Reads and audits the model"));
        mutator.SetProperty(Set("modelPermission", "Administrator"));
        mutator.SetProperty(Set("name", "Auditors"));

        Assert.Equal("Auditors", role.Name);
        Assert.Equal("Reads and audits the model", role.Description);
        Assert.Equal(ModelPermission.Administrator, role.ModelPermission);
    }

    [Fact]
    public void SetProperty_ModelPermission_RejectsUnknownEnumName()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("modelPermission", "Bogus")));

        Assert.Contains("must be one of: None, Read, ReadRefresh, Refresh, Administrator", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, role) = NewModel();

        mutator.SetProperty(Set("description", "Reads and audits the model"));
        mutator.SetProperty(Set("modelPermission", "Administrator"));

        var snapshot = TomModelSummarizer.Snapshot((Database)role.Model.Database, "M");
        var roleObject = snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Role);
        var projected = ModelPropertyCatalog.Project(roleObject);

        // modelPermission is the role's Detail, so the descriptor reads it from there.
        Assert.Equal("Administrator", projected["modelPermission"]);
        Assert.Equal("Reads and audits the model", projected["description"]);
    }

    [Fact]
    public void SetProperty_UnknownRoleProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("bogus", "x")));

        Assert.Contains("modelPermission", ex.Message);
        Assert.Contains("description", ex.Message);
    }

    private static ModelObjectSetRequest Set(string property, string value)
        => new("Readers", [new ModelPropertyAssignment(property, value)], ModelObjectKind.Role);

    private static (TomModelMutator Mutator, ModelRole Role) NewModel()
    {
        var db = NewDatabase(compatibilityLevel: 1702);
        var role = new ModelRole { Name = "Readers" };
        db.Model.Roles.Add(role);
        return (new TomModelMutator(db), role);
    }
}
