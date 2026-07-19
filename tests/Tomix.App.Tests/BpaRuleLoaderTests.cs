using Tomix.App.Bpa;

namespace Tomix.App.Tests;

public sealed class BpaRuleLoaderTests
{
    [Fact]
    public void LoadBundledCatalog_LoadsFullCatalog()
    {
        var rules = BpaRuleLoader.LoadBundledCatalog();

        Assert.True(rules.Count > 50);
        Assert.Contains(rules, r => r.Id == "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS");
    }

    [Fact]
    public async Task LoadRulesetAsync_Standard_LoadsCuratedSubsetOfBundledCatalog()
    {
        var standard = await BpaRuleLoader.LoadRulesetAsync("standard", CancellationToken.None);
        var catalog = BpaRuleLoader.LoadBundledCatalog();

        // Exactly the curated IDs must resolve against the catalog — a drop below 27 means a
        // curated ID drifted from the bundled rule IDs.
        Assert.Equal(27, standard.Count);
        Assert.True(standard.Count < catalog.Count);
        Assert.Contains(standard, r => r.Id == "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS");

        // Style-opinion rules stay out of the curated default.
        Assert.DoesNotContain(standard, r => r.Id == "OBJECTS_WITH_NO_DESCRIPTION");
        Assert.DoesNotContain(standard, r => r.Id == "FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED");

        // Destructive-fix maintenance rules stay out of the curated default.
        Assert.DoesNotContain(standard, r => r.Id == "UNNECESSARY_COLUMNS");
        Assert.DoesNotContain(standard, r => r.Id == "UNNECESSARY_MEASURES");
    }

    [Fact]
    public async Task LoadRulesetAsync_Full_LoadsEntireBundledCatalog()
    {
        var full = await BpaRuleLoader.LoadRulesetAsync("full", CancellationToken.None);

        Assert.Equal(BpaRuleLoader.LoadBundledCatalog().Count, full.Count);
        Assert.Contains(full, r => r.Id == "UNNECESSARY_COLUMNS");
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

    [Fact]
    public async Task LoadFromSourceAsync_Http_UsesSuppliedClient()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        var rules = await BpaRuleLoader.LoadFromSourceAsync(
            "https://example.test/rules.json",
            client,
            CancellationToken.None);

        Assert.True(handler.Called);
        Assert.Contains(handler.UserAgent, value => value.Contains("tomix-cli", StringComparison.Ordinal));
        Assert.Equal("REMOTE_RULE", Assert.Single(rules).Id);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }
        public IReadOnlyList<string> UserAgent { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Called = true;
            UserAgent = request.Headers.UserAgent.Select(value => value.ToString()).ToList();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"ID":"REMOTE_RULE","Name":"Remote","Category":"Custom","Severity":1,"Scope":"Model"}]""")
            });
        }
    }
}
