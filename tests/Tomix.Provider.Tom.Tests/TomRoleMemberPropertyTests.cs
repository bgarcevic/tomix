using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Role-member property coverage for set: the identity fields (memberId always; identityProvider
/// and memberType on external members only, with a tailored error for Windows members) and
/// read-back through the summarizer.
/// </summary>
public sealed class TomRoleMemberPropertyTests
{
    [Fact]
    public void SetProperty_ExternalMember_IdentityProperties_Apply()
    {
        var (mutator, role) = NewModel();

        mutator.SetProperty(Set("user@contoso.com", "memberId", "aad-object-id-2"));
        mutator.SetProperty(Set("user@contoso.com", "identityProvider", "EntraId"));
        mutator.SetProperty(Set("user@contoso.com", "memberType", "Group"));

        var external = Assert.IsType<ExternalModelRoleMember>(role.Members.Single(m => m.MemberName == "user@contoso.com"));
        Assert.Equal("aad-object-id-2", external.MemberID);
        Assert.Equal("EntraId", external.IdentityProvider);
        Assert.Equal(RoleMemberType.Group, external.MemberType);
    }

    [Fact]
    public void SetProperty_WindowsMember_IdentityProperties_Rejected()
    {
        var (mutator, _) = NewModel();

        var provider = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set(@"CONTOSO\jane", "identityProvider", "AzureAD")));
        Assert.Contains("only supported for external role members", provider.Message);
        Assert.Contains("Windows member", provider.Message);

        var type = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set(@"CONTOSO\jane", "memberType", "Group")));
        Assert.Contains("only supported for external role members", type.Message);
    }

    [Fact]
    public void SetProperty_WindowsMember_HintOmitsExternalTokens()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set(@"CONTOSO\jane", "bogus", "x")));

        Assert.Contains("memberId", ex.Message);
        Assert.DoesNotContain("identityProvider", ex.Message);
        Assert.DoesNotContain("memberType", ex.Message);
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, role) = NewModel();

        mutator.SetProperty(Set("user@contoso.com", "memberId", "aad-object-id-2"));
        mutator.SetProperty(Set("user@contoso.com", "memberType", "Group"));

        var snapshot = TomModelSummarizer.Snapshot((Database)role.Model.Database, "M");
        var members = snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Role).Children
            .Where(c => c.Kind == ModelObjectKind.RoleMember).ToList();
        var externalProjected = ModelPropertyCatalog.Project(members.Single(m => m.Name == "user@contoso.com"));
        var windowsProjected = ModelPropertyCatalog.Project(members.Single(m => m.Name == @"CONTOSO\jane"));

        Assert.Equal("aad-object-id-2", externalProjected["memberId"]);
        Assert.Equal("AzureAD", externalProjected["identityProvider"]);
        Assert.Equal("Group", externalProjected["memberType"]);
        // Windows members have no identity-provider fields, and set rejects them there too.
        Assert.Equal("", windowsProjected["identityProvider"]);
        Assert.Equal("", windowsProjected["memberType"]);
    }

    [Fact]
    public void SetProperty_ExternalMember_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("user@contoso.com", "bogus", "x")));

        Assert.Contains("memberId", ex.Message);
        Assert.Contains("identityProvider", ex.Message);
        Assert.Contains("memberType", ex.Message);
    }

    private static ModelObjectSetRequest Set(string member, string property, string value)
        => new($"Readers/{member}", [new ModelPropertyAssignment(property, value)], ModelObjectKind.RoleMember);

    private static (TomModelMutator Mutator, ModelRole Role) NewModel()
    {
        var db = NewDatabase(compatibilityLevel: 1702);
        var role = new ModelRole { Name = "Readers" };
        role.Members.Add(new ExternalModelRoleMember { MemberName = "user@contoso.com", IdentityProvider = "AzureAD" });
        role.Members.Add(new WindowsModelRoleMember { MemberName = @"CONTOSO\jane" });
        db.Model.Roles.Add(role);
        return (new TomModelMutator(db), role);
    }
}
