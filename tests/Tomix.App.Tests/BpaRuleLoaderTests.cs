using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;

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

        // Exactly the curated IDs must resolve against the catalog — a drop below 26 means a
        // curated ID drifted from the bundled rule IDs.
        Assert.Equal(26, standard.Count);
        Assert.True(standard.Count < catalog.Count);
        Assert.Contains(standard, r => r.Id == "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS");
    }

    [Theory]
    // Style-opinion rules.
    [InlineData("OBJECTS_WITH_NO_DESCRIPTION")]
    [InlineData("FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED")]
    [InlineData("NUMERIC_COLUMN_SUMMARIZE_BY")]
    // Destructive-fix maintenance rules.
    [InlineData("UNNECESSARY_COLUMNS")]
    [InlineData("UNNECESSARY_MEASURES")]
    // Noisy or heuristic rules that fire on most real models.
    [InlineData("ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS")]
    [InlineData("AVOID_FLOATING_POINT_DATA_TYPES")]
    [InlineData("USE_THE_DIVIDE_FUNCTION_FOR_DIVISION")]
    [InlineData("DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE")]
    public async Task LoadRulesetAsync_Standard_LeavesOptInRulesToFull(string ruleId)
    {
        var standard = await BpaRuleLoader.LoadRulesetAsync("standard", CancellationToken.None);
        var full = await BpaRuleLoader.LoadRulesetAsync("full", CancellationToken.None);

        Assert.DoesNotContain(standard, r => r.Id == ruleId);
        Assert.Contains(full, r => r.Id == ruleId);
    }

    [Fact]
    public async Task LoadRulesetAsync_Standard_ErrorSeverityIsReservedForErrorPrevention()
    {
        // The deploy gate blocks on error severity by default, so a curated error-severity rule
        // must flag a broken model, not a style or performance preference.
        var standard = await BpaRuleLoader.LoadRulesetAsync("standard", CancellationToken.None);

        var misfiled = standard
            .Where(r => r.Severity == RuleSeverity.Error && r.Category != "Error Prevention")
            .Select(r => r.Id);
        Assert.Empty(misfiled);
    }

    [Theory]
    [InlineData("RELATIONSHIP_COLUMNS_SAME_DATA_TYPE")]
    [InlineData("DAX_COLUMNS_FULLY_QUALIFIED")]
    [InlineData("DAX_MEASURES_UNQUALIFIED")]
    [InlineData("PROVIDE_FORMAT_STRING_FOR_MEASURES")]
    [InlineData("OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE")]
    [InlineData("AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY")]
    public void LoadBundledCatalog_AdvisoryRules_AreWarningsSoTheyDoNotBlockDeploy(string ruleId)
    {
        var rule = BpaRuleLoader.LoadBundledCatalog().Single(r => r.Id == ruleId);

        Assert.Equal(RuleSeverity.Warning, rule.Severity);
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
