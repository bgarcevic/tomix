using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Contract for <c>samples/deploy-qa</c>: the fixture only earns its keep if it carries every
/// aspect granular deployment can preserve. Drop one and the matching live QA cell in
/// <c>scripts/qa/deploy-script-matrix.sh</c> silently goes vacuous — the check reports
/// "inconclusive", which is a warning, so the cell still reads as clean. That is exactly how a
/// previous QA run proved almost nothing while every cell passed.
///
/// Assertions are made against the fixture serialized as TMSL, the same form
/// <see cref="TmslDeployScriptBuilder"/> merges, so anything this test sees the merge sees too.
/// </summary>
public sealed class DeployQaFixtureTests
{
    private static readonly JsonObject Model = LoadFixtureModel();

    [Fact]
    public void CarriesSharedExpressions_IncludingTheRefreshPolicyBounds()
    {
        var names = Names(Model["expressions"]);

        Assert.Contains("RangeStart", names);
        Assert.Contains("RangeEnd", names);
        // Beyond the policy bounds there must be at least one environment-specific parameter,
        // and more than one so preservation is visibly per-name rather than whole-collection.
        Assert.True(
            names.Count(n => n is not ("RangeStart" or "RangeEnd")) >= 2,
            $"expected 2+ non-bound expressions, got: {string.Join(", ", names)}");
    }

    [Fact]
    public void CarriesRoles_WithATablePermission()
    {
        var roles = Model["roles"]?.AsArray();

        Assert.NotNull(roles);
        Assert.True(roles.Count >= 2, "two roles, so role preservation is visibly whole-collection");
        Assert.Contains(
            roles.OfType<JsonObject>(),
            r => r["tablePermissions"] is JsonArray p && p.Count > 0);
    }

    [Fact]
    public void CarriesARefreshPolicyTableWithASourceExpression()
    {
        // The exemption that protects processed incremental-refresh data keys off exactly this:
        // a refreshPolicy carrying a sourceExpression on the target table.
        Assert.Contains(
            Tables(),
            t => t["refreshPolicy"] is JsonObject policy && policy["sourceExpression"] is not null);
    }

    [Fact]
    public void CarriesATableWithMultiplePartitions()
    {
        // Partitions deploy as a whole collection, not merged per name. One partition per table
        // cannot tell those apart.
        Assert.Contains(Tables(), t => t["partitions"] is JsonArray p && p.Count >= 2);
    }

    [Fact]
    public void CarriesACalculatedTable()
    {
        // Calculated partitions always come from the source whatever the flags say; without one
        // the IsQueryTable branch is never exercised live.
        Assert.Contains(
            Tables(),
            t => t["partitions"]?.AsArray().OfType<JsonObject>().Any(
                p => p["source"]?["type"]?.GetValue<string>() == "calculated") == true);
    }

    private static IEnumerable<JsonObject> Tables()
        => Model["tables"]?.AsArray().OfType<JsonObject>() ?? [];

    private static List<string> Names(JsonNode? collection)
        => collection?.AsArray().OfType<JsonObject>()
            .Select(o => o["name"]?.GetValue<string>() ?? "")
            .ToList() ?? [];

    private static JsonObject LoadFixtureModel()
    {
        var folder = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "deploy-qa"));
        Assert.True(Directory.Exists(folder), $"fixture not found: {folder}");

        var database = TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        var json = TabularJsonSerializer.SerializeDatabase(database, new SerializeOptions());

        return JsonNode.Parse(json)!.AsObject()["model"]!.AsObject();
    }
}
