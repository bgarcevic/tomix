using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The credential-redaction contract on the deploy script: a script that goes straight to the
/// engine (<c>forExecution: true</c>) must carry restricted information — preserved connection
/// strings and the credentials they bind survive the deploy — while script output
/// (<c>--xmla</c> to a file or stdout) must never hold a credential that could land in a CI
/// artifact, a log, or a working directory. These tests drive the real <c>BuildScript</c>
/// serialization with detached in-memory databases (no server, no auth) and a sentinel
/// password, so flipping the flag in either direction fails here rather than shipping a
/// password.
/// </summary>
public sealed class TomModelDeployScriptRedactionTests
{
    private const string SourceCredential = "Data Source=dev;Password=pw-source";
    private const string TargetCredential = "Data Source=prod;Password=pw-target";

    /// <summary>A direct deploy of a credentialed source must ship the full connection string,
    /// or the target ends up with a credential-less data source that cannot connect.</summary>
    [Fact]
    public void BuildScript_ForExecution_IncludesSourceCredentials()
    {
        var script = TomModelDeployer.BuildScript(
            Source(), existing: null, "Prod", Request(), forExecution: true);

        Assert.Contains(SourceCredential, ConnectionStrings(script));
    }

    /// <summary>The --xmla path must not leak the source's credential into the script, even
    /// though the same source carries it when executed.</summary>
    [Fact]
    public void BuildScript_ForOutput_OmitsSourceCredentials()
    {
        var script = TomModelDeployer.BuildScript(
            Source(), existing: null, "Prod", Request(), forExecution: false);

        Assert.DoesNotContain("pw-source", script);
    }

    /// <summary>A preserved target data source is copied verbatim, so its credential must be
    /// present in the executed script or the preserved connection would break.</summary>
    [Fact]
    public void BuildScript_ForExecution_IncludesTargetCredentials()
    {
        var script = TomModelDeployer.BuildScript(
            Source(), Target(), "Prod", Request(), forExecution: true);

        Assert.Contains(TargetCredential, ConnectionStrings(script));
    }

    /// <summary>The --xmla path reads the target's real connection strings (with credentials)
    /// to preserve them, so the redaction must strip them from what gets written out.</summary>
    [Fact]
    public void BuildScript_ForOutput_OmitsTargetCredentials()
    {
        var script = TomModelDeployer.BuildScript(
            Source(), Target(), "Prod", Request(), forExecution: false);

        Assert.DoesNotContain("pw-target", script);
    }

    /// <summary>Pins the call-site wiring through the public offline path: what
    /// <c>tx deploy --xmla</c> writes for a credentialed source is the credential-free script.</summary>
    [Fact]
    public async Task GenerateScriptAsync_FullOptions_OfflineScriptOmitsCredentials()
    {
        var script = await TomModelDeployer.GenerateScriptAsync(
            Source(), Request(ModelDeployOptions.Full), tokenProvider: null, CancellationToken.None);

        Assert.DoesNotContain("pw-source", script);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static ModelDeployRequest Request(ModelDeployOptions? options = null)
        => new("localhost:59962", "Prod", CreateOnly: false, Force: false, options);

    private static Database Source() => Credentialed("Source", "DevWarehouse", SourceCredential);

    private static Database Target() => Credentialed("Prod", "Warehouse", TargetCredential);

    private static Database Credentialed(string name, string dataSourceName, string connectionString)
    {
        var db = NewDatabase(name, 1601);
        AddTable(db, "Sales");
        db.Model.DataSources.Add(new ProviderDataSource
        {
            Name = dataSourceName,
            ConnectionString = connectionString
        });
        return db;
    }

    private static List<string> ConnectionStrings(string script)
    {
        var model = JsonNode.Parse(script)?["createOrReplace"]?["database"]?["model"];
        return model?["dataSources"] is not JsonArray sources
            ? []
            : sources.OfType<JsonObject>()
                .Select(o => o["connectionString"]?.GetValue<string>() ?? "")
                .ToList();
    }
}
