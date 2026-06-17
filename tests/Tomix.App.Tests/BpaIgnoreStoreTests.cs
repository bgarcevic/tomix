using System.Text.Json;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaIgnoreStoreTests
{
    [Fact]
    public void ParseRuleIds_ReadsRuleIdsCaseInsensitively()
    {
        var ids = BpaIgnoreStore.ParseRuleIds("{\"RuleIDs\":[\"RULE_A\",\"rule_b\"]}");

        Assert.Contains("rule_a", ids);
        Assert.Contains("RULE_B", ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"RuleIDs\":\"oops-not-an-array\"}")]
    public void ParseRuleIds_MalformedOrEmpty_ReturnsEmptyWithoutThrowing(string? value)
        => Assert.Empty(BpaIgnoreStore.ParseRuleIds(value));

    [Fact]
    public void ReadRuleIds_PrefersCorrectKeyOverLegacy()
    {
        var obj = ObjectWith(new Dictionary<string, string>
        {
            [$"Annotation:{BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"CORRECT\"]}",
            [$"Annotation:{BpaIgnoreStore.LegacyKey}"] = "{\"RuleIDs\":[\"LEGACY\"]}"
        });

        var ids = BpaIgnoreStore.ReadRuleIds(obj);

        Assert.Contains("CORRECT", ids);
        Assert.DoesNotContain("LEGACY", ids);
    }

    [Fact]
    public void ReadRuleIds_FallsBackToLegacyKeyWhenCorrectAbsent()
    {
        var obj = ObjectWith(new Dictionary<string, string>
        {
            [$"Annotation:{BpaIgnoreStore.LegacyKey}"] = "{\"RuleIDs\":[\"LEGACY\"]}"
        });

        Assert.Contains("LEGACY", BpaIgnoreStore.ReadRuleIds(obj));
    }

    [Fact]
    public void Serialize_RoundTripsAndDedupes()
    {
        var json = BpaIgnoreStore.Serialize(["RULE_A", "RULE_A", "RULE_B"]);

        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("RuleIDs").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { "RULE_A", "RULE_B" }, BpaIgnoreStore.ParseRuleIds(json).OrderBy(x => x));
    }

    private static ModelObject ObjectWith(Dictionary<string, string> properties)
        => new("X", ModelObjectKind.Column, "T/X",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [], Properties: properties);
}
