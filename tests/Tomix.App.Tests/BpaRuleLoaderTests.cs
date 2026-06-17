using Tomix.App.Bpa;

namespace Tomix.App.Tests;

public sealed class BpaRuleLoaderTests
{
    [Fact]
    public void LoadDefaultRules_LoadsBundledStandardCatalog()
    {
        var rules = BpaRuleLoader.LoadDefaultRules();

        Assert.True(rules.Count > 50);
        Assert.Contains(rules, r => r.Id == "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS");
    }

    [Fact]
    public async Task LoadRulesetAsync_Standard_LoadsBundledStandardCatalog()
    {
        var rules = await BpaRuleLoader.LoadRulesetAsync("standard", CancellationToken.None);

        Assert.True(rules.Count > 50);
        Assert.Contains(rules, r => r.Id == "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS");
    }

    [Fact]
    public async Task LoadFromSourceAsync_File_LoadsCustomCatalog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tomix-bpa-rules-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            """
            [
              {
                "ID": "CUSTOM_RULE",
                "Name": "Custom rule",
                "Category": "Custom",
                "Severity": 2,
                "Scope": "Measure"
              }
            ]
            """);

        try
        {
            var rules = await BpaRuleLoader.LoadFromSourceAsync(path, CancellationToken.None);

            var rule = Assert.Single(rules);
            Assert.Equal("CUSTOM_RULE", rule.Id);
            Assert.Equal("Measure", Assert.Single(rule.Scope));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
