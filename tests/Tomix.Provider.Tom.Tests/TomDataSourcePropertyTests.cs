using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Data source property coverage for set: the non-secret writable scalars split by source
/// kind (provider fields, the structured context expression, and the shared connection cap),
/// the rejection of credential fields, and read-back through the summarizer.
/// </summary>
public sealed class TomDataSourcePropertyTests
{
    [Fact]
    public void SetProperty_ProviderSource_ScalarProperties_Apply()
    {
        var (mutator, db) = NewModel();

        mutator.SetProperty(Set("DataSources/PDS", "maxConnections", "5"));
        mutator.SetProperty(Set("DataSources/PDS", "impersonationMode", "ImpersonateCurrentUser"));
        mutator.SetProperty(Set("DataSources/PDS", "isolation", "Snapshot"));
        mutator.SetProperty(Set("DataSources/PDS", "timeout", "30"));

        var provider = (ProviderDataSource)db.Model.DataSources["PDS"];
        Assert.Equal(5, provider.MaxConnections);
        Assert.Equal(ImpersonationMode.ImpersonateCurrentUser, provider.ImpersonationMode);
        Assert.Equal(DatasourceIsolation.Snapshot, provider.Isolation);
        Assert.Equal(30, provider.Timeout);
    }

    [Fact]
    public void SetProperty_StructuredSource_ScalarProperties_Apply()
    {
        var (mutator, db) = NewModel();

        mutator.SetProperty(Set("DataSources/SDS", "maxConnections", "3"));
        mutator.SetProperty(Set("DataSources/SDS", "contextExpression", "true"));

        var structured = (StructuredDataSource)db.Model.DataSources["SDS"];
        Assert.Equal(3, structured.MaxConnections);
        Assert.Equal("true", structured.ContextExpression);
    }

    [Fact]
    public void SetProperty_Credentials_RejectedWithSecretsGuidance()
    {
        var (mutator, _) = NewModel();

        var connectionString = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("DataSources/PDS", "connectionString", "Data Source=evil")));
        Assert.Contains("secrets are never accepted via argv", connectionString.Message);

        var password = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("DataSources/PDS", "password", "hunter2")));
        Assert.Contains("secrets are never accepted via argv", password.Message);
    }

    [Fact]
    public void SetProperty_SourceBoundProperties_OmittedFromTheOtherKindHint()
    {
        var (mutator, _) = NewModel();

        var provider = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("DataSources/PDS", "contextExpression", "true")));
        Assert.Contains("impersonationMode", Hint(provider.Message));
        Assert.DoesNotContain("contextExpression", Hint(provider.Message));

        var structured = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("DataSources/SDS", "impersonationMode", "ImpersonateCurrentUser")));
        Assert.Contains("contextExpression", Hint(structured.Message));
        Assert.DoesNotContain("impersonationMode", Hint(structured.Message));
    }

    [Fact]
    public void SetProperty_NewProperties_ReadBackFromSnapshot()
    {
        var (mutator, db) = NewModel();

        mutator.SetProperty(Set("DataSources/PDS", "maxConnections", "5"));
        mutator.SetProperty(Set("DataSources/PDS", "impersonationMode", "ImpersonateCurrentUser"));
        mutator.SetProperty(Set("DataSources/PDS", "isolation", "Snapshot"));
        mutator.SetProperty(Set("DataSources/PDS", "timeout", "30"));
        mutator.SetProperty(Set("DataSources/SDS", "contextExpression", "true"));

        var snapshot = TomModelSummarizer.Snapshot(db, "M");
        var providerProjected = ModelPropertyCatalog.Project(snapshot.Objects.Single(o => o.Path == "DataSources/PDS"));
        var structuredProjected = ModelPropertyCatalog.Project(snapshot.Objects.Single(o => o.Path == "DataSources/SDS"));

        Assert.Equal(5, providerProjected["maxConnections"]);
        Assert.Equal("ImpersonateCurrentUser", providerProjected["impersonationMode"]);
        Assert.Equal("Snapshot", providerProjected["isolation"]);
        Assert.Equal(30, providerProjected["timeout"]);
        Assert.Equal("true", structuredProjected["contextExpression"]);
        // Provider fields surface empty on the structured source, and vice versa.
        Assert.Equal("", structuredProjected["impersonationMode"]);
        Assert.Equal("", providerProjected["contextExpression"]);
    }

    [Fact]
    public void SetProperty_UnknownDataSourceProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() =>
            mutator.SetProperty(Set("DataSources/PDS", "bogus", "x")));

        var hint = Hint(ex.Message);
        Assert.Contains("maxConnections", hint);
        Assert.Contains("impersonationMode", hint);
        Assert.Contains("timeout", hint);
    }

    private static string Hint(string message)
        => message[message.IndexOf("Writable properties:")..];

    private static ModelObjectSetRequest Set(string path, string property, string value)
        => new(path, [new ModelPropertyAssignment(property, value)], ModelObjectKind.DataSource);

    private static (TomModelMutator Mutator, Database Database) NewModel()
    {
        var db = NewDatabase(compatibilityLevel: 1702);
        db.Model.DataSources.Add(new ProviderDataSource
        {
            Name = "PDS",
            Provider = "SQLNCLI11",
            ConnectionString = "Data Source=sql01"
        });
        db.Model.DataSources.Add(new StructuredDataSource { Name = "SDS" });
        return (new TomModelMutator(db), db);
    }
}
