using Tomix.App.Bpa;
using Tomix.Core.Bpa;

namespace Tomix.App.Tests;

public sealed class BpaRuleResolverTests
{
    // Category is used as a marker so a winning occurrence can be traced back to its source.
    private static BpaRule Rule(string id, string marker)
        => new(id, id, marker, BpaSeverity.Warning, ["Table"], Expression: "true");

    private static BpaRule? Find(IReadOnlyList<BpaRule> rules, string id)
        => rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Resolve_ModelEmbeddedWinsOverAllOtherSources()
    {
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "machine", [Rule("DUP", "machine")]),
            new BpaRuleCollection(BpaRuleSourceKind.User, "user", [Rule("DUP", "user")]),
            new BpaRuleCollection(BpaRuleSourceKind.External, "external", [Rule("DUP", "external")]),
            new BpaRuleCollection(BpaRuleSourceKind.ModelEmbedded, "embedded", [Rule("DUP", "embedded")])
        ]);

        var winner = Assert.Single(rules);
        Assert.Equal("embedded", winner.Category);
    }

    [Fact]
    public void Resolve_PrecedenceLadder_HigherKindWins()
    {
        // external beats user beats machine for the same id.
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "m", [Rule("DUP", "machine")]),
            new BpaRuleCollection(BpaRuleSourceKind.User, "u", [Rule("DUP", "user")]),
            new BpaRuleCollection(BpaRuleSourceKind.External, "e", [Rule("DUP", "external")])
        ]);

        Assert.Equal("external", Find(rules, "DUP")!.Category);
    }

    [Fact]
    public void Resolve_EarlierExternalCollectionWins()
    {
        // Two external collections in declared order: the earlier one wins (spec §7).
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.External, "first", [Rule("DUP", "first")]),
            new BpaRuleCollection(BpaRuleSourceKind.External, "second", [Rule("DUP", "second")])
        ]);

        Assert.Equal("first", Assert.Single(rules).Category);
    }

    [Fact]
    public void Resolve_LaterCollectionWins_ForNonExternalKinds()
    {
        // Within the same (non-external) kind, a later collection overrides an earlier one.
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.User, "first", [Rule("DUP", "first")]),
            new BpaRuleCollection(BpaRuleSourceKind.User, "second", [Rule("DUP", "second")])
        ]);

        Assert.Equal("second", Assert.Single(rules).Category);
    }

    [Fact]
    public void Resolve_DedupesRuleIdsCaseInsensitively()
    {
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "m", [Rule("rule_a", "machine")]),
            new BpaRuleCollection(BpaRuleSourceKind.User, "u", [Rule("RULE_A", "user")])
        ]);

        var winner = Assert.Single(rules);
        Assert.Equal("user", winner.Category);
    }

    [Fact]
    public void Resolve_OutputOrderIsDeterministic_BySourceRankThenEncounter()
    {
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.ModelEmbedded, "e", [Rule("Z", "e")]),
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "m", [Rule("B", "m"), Rule("A", "m")])
        ]);

        // Machine (rank 1) rules come before the model-embedded (rank 4) rule, in original order.
        Assert.Equal(["B", "A", "Z"], rules.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_SkipsRulesWithBlankId()
    {
        var rules = BpaRuleResolver.Resolve(
        [
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "m", [Rule("", "machine"), Rule("KEEP", "machine")])
        ]);

        Assert.Equal(["KEEP"], rules.Select(r => r.Id));
    }
}
