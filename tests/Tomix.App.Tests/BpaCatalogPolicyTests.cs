using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.App.Tests;

/// <summary>
/// #233 catalog policy: the catalog grows through default-off categories that a
/// <c>--ruleset</c> preset opts into, and the two bundled IsAvailableInMdx rules point the same
/// way, so the deploy gate never contradicts a <c>--ruleset full</c> run.
/// </summary>
public sealed class BpaCatalogPolicyTests
{
    private static BpaRule Rule(string id, string category)
        => new(id, id, category, RuleSeverity.Warning, ["Table"], Expression: "true");

    private static readonly IReadOnlyList<BpaRule> Catalog =
    [
        Rule("AVOID_INVALID_NAME_CHARACTERS", "Error Prevention"),
        Rule("OBJECTS_WITH_NO_DESCRIPTION", "Maintenance"),
        Rule("TRANSLATE_VISIBLE_NAMES", "Localization"),
    ];

    [Theory]
    [InlineData("standard")]
    [InlineData("full")]
    public void DefaultOffCategory_IsLeftOutOfTheDefaultPresets(string preset)
    {
        var rules = BpaRuleLoader.SelectBundled(Catalog, preset);

        Assert.NotNull(rules);
        Assert.DoesNotContain(rules, r => r.Category == "Localization");
    }

    [Fact]
    public void DefaultOffCategory_ItsOwnPresetSelectsOnlyThatCategory()
    {
        var rules = BpaRuleLoader.SelectBundled(Catalog, "localization");

        Assert.Equal(["TRANSLATE_VISIBLE_NAMES"], rules!.Select(r => r.Id));
    }

    [Fact]
    public void CategoryPresets_AreOfferedOnlyForDefaultOffCategoriesTheCatalogHasRulesFor()
    {
        Assert.Equal(["localization"], BpaRuleLoader.CategoryPresets(Catalog));
        Assert.Empty(BpaRuleLoader.CategoryPresets([Rule("X", "Maintenance")]));
    }

    [Theory]
    [InlineData("standard", new[] { "standard" })]
    [InlineData("standard,localization", new[] { "standard", "localization" })]
    [InlineData(" full , localization ,", new[] { "full", "localization" })]
    [InlineData(null, new[] { "standard" })]
    [InlineData("", new[] { "standard" })]
    public void SplitRulesets_ReadsACommaSeparatedPresetList(string? value, string[] expected)
        => Assert.Equal(expected, BpaRuleLoader.SplitRulesets(value));

    [Fact]
    public void BundledCatalog_NoCuratedRuleSitsInADefaultOffCategory()
    {
        var curated = BpaRuleLoader.SelectBundled(BpaRuleLoader.LoadBundledCatalog(), "standard")!;

        Assert.DoesNotContain(curated, r => BpaRuleLoader.DefaultOffCategories.Contains(r.Category));
    }

    [Fact]
    public async Task LoadRulesetAsync_UnknownPreset_MessageIsReadyToShow()
    {
        // The message is printed as-is, so it must not carry ArgumentException's "(Parameter ...)" suffix.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => BpaRuleLoader.LoadRulesetAsync("standard,nope", CancellationToken.None));

        Assert.EndsWith("Use --rules for a custom file or URL.", ex.Message);
    }

    [Fact]
    public async Task LoadRulesetAsync_ComposesSeveralPresets()
    {
        var composed = await BpaRuleLoader.LoadRulesetAsync("standard,full", CancellationToken.None);

        Assert.Equal(
            BpaRuleLoader.LoadBundledCatalog().Count,
            composed.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // --- IsAvailableInMdx direction ------------------------------------------------------

    private const string TrueRuleId = "SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS";
    private const string FalseRuleId = "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS";

    [Fact]
    public async Task IsAvailableInMdx_TheDeployGateEnforcesTrueOnNecessaryColumnsAndNotTheOptimization()
    {
        // The deploy gate runs the standard ruleset and blocks on error severity. Turning an
        // attribute hierarchy off where one is needed breaks the model, so that rule is a
        // blocking error there; turning it off elsewhere is a warning-level optimization that
        // stays opt-in through --ruleset full.
        var standard = await BpaRuleLoader.LoadRulesetAsync("standard", CancellationToken.None);
        var catalog = BpaRuleLoader.LoadBundledCatalog();

        Assert.Equal(RuleSeverity.Error, Assert.Single(standard, r => r.Id == TrueRuleId).Severity);
        Assert.DoesNotContain(standard, r => r.Id == FalseRuleId);
        Assert.Equal(RuleSeverity.Warning, catalog.Single(r => r.Id == FalseRuleId).Severity);
    }

    public static TheoryData<string, bool, bool> ColumnShapes()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var usage in new[] { "none", "hierarchy", "sortBy", "sortedBy", "variation" })
            foreach (var hidden in new[] { false, true })
                foreach (var mdx in new[] { false, true })
                    data.Add(usage, hidden, mdx);
        return data;
    }

    [Theory]
    [MemberData(nameof(ColumnShapes))]
    public void IsAvailableInMdx_NoColumnShapeViolatesBothRules_AndTheFalseFixNeverTripsTheTrueRule(
        string usage, bool hidden, bool isAvailableInMdx)
    {
        var snapshot = Snapshot(usage, hidden, isAvailableInMdx);
        var rules = BpaRuleLoader.LoadBundledCatalog().Where(r => r.Id is TrueRuleId or FalseRuleId).ToList();

        var flagged = Flagged(rules, snapshot);
        Assert.False(
            flagged.Contains((TrueRuleId, "T/Subject")) && flagged.Contains((FalseRuleId, "T/Subject")),
            "a column must never be told to be both available and unavailable in MDX");

        // Apply what the optimization asks for: the necessary-column rule must stay quiet.
        if (flagged.Contains((FalseRuleId, "T/Subject")))
            Assert.DoesNotContain((TrueRuleId, "T/Subject"), Flagged(rules, Snapshot(usage, hidden, isAvailableInMdx: false)));
    }

    private static HashSet<(string RuleId, string Path)> Flagged(IReadOnlyList<BpaRule> rules, ModelSnapshot snapshot)
        => new BpaEngine()
            .Evaluate(snapshot, new BpaEngineOptions(rules))
            .Violations
            .Select(v => (v.RuleId, v.ObjectPath))
            .ToHashSet();

    private static ModelSnapshot Snapshot(string usage, bool hidden, bool isAvailableInMdx)
    {
        var subjectProps = new Dictionary<string, string>();
        var otherProps = new Dictionary<string, string>();
        switch (usage)
        {
            case "hierarchy": subjectProps["UsedInHierarchies"] = "H"; break;
            case "variation": subjectProps["UsedInVariations"] = "V"; break;
            case "sortBy": otherProps["SortByColumn"] = "Subject"; break;      // another column sorts by the subject
            case "sortedBy": subjectProps["SortByColumn"] = "Other"; break;    // the subject sorts by another column
        }

        // "Other" always keeps its hierarchy so it never contributes findings of its own.
        return new ModelSnapshot("M", 1601,
        [
            new ModelObject("T", ModelObjectKind.Table, "T",
                Detail: null, Expression: null, Description: "desc", Hidden: false, SourceColumn: null,
                Children:
                [
                    Column("Subject", hidden, isAvailableInMdx, subjectProps),
                    Column("Other", hidden: false, isAvailableInMdx: true, otherProps),
                ],
                Properties: new Dictionary<string, string> { ["ObjectType"] = "Table", ["TableObjectType"] = "Table" })
        ]);
    }

    private static ModelObject Column(string name, bool hidden, bool isAvailableInMdx, Dictionary<string, string> extra)
    {
        var props = new Dictionary<string, string>(extra)
        {
            ["DataType"] = "String",
            ["ObjectType"] = "DataColumn",
            ["SourceColumn"] = name,
            ["IsAvailableInMDX"] = isAvailableInMdx ? "true" : "false",
        };
        return new ModelObject(name, ModelObjectKind.Column, $"T/{name}",
            Detail: null, Expression: null, Description: "desc", Hidden: hidden, SourceColumn: name,
            Children: [], Properties: props);
    }
}
