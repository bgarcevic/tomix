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

    [Fact]
    public void ResolveWithSources_CountsTheRulesEachSourceContributes()
    {
        // #233: the attribution line counts effective rules per source, so a rule a later source
        // overrides is credited to the source that won, not to both.
        var resolved = BpaRuleResolver.ResolveWithSources(
        [
            new BpaRuleCollection(BpaRuleSourceKind.Machine, "standard", [Rule("A", "m"), Rule("DUP", "m")], BpaRuleOrigin.Ruleset),
            new BpaRuleCollection(BpaRuleSourceKind.User, "team.json", [Rule("DUP", "u"), Rule("B", "u")], BpaRuleOrigin.Environment)
        ]);

        Assert.Equal(3, resolved.Rules.Count);
        Assert.Equal(
            [("standard", BpaRuleOrigin.Ruleset, 1, 1), ("team.json", BpaRuleOrigin.Environment, 2, 0)],
            resolved.Sources.Select(s => (s.Name, s.Origin, s.Rules, s.Overridden)));
    }

    [Fact]
    public void ResolveWithSources_KeepsASourceWhoseRulesWereAllOverridden()
    {
        // A configured source that contributes nothing is still named (as overridden), so a CI log never
        // hides that a file was loaded and lost every id to a higher-precedence source.
        var resolved = BpaRuleResolver.ResolveWithSources(
        [
            new BpaRuleCollection(BpaRuleSourceKind.User, "shadowed.json", [Rule("DUP", "u")], BpaRuleOrigin.Config),
            new BpaRuleCollection(BpaRuleSourceKind.ModelEmbedded, "model-embedded", [Rule("DUP", "e")], BpaRuleOrigin.Model)
        ]);

        Assert.Equal([("shadowed.json", 0, 1), ("model-embedded", 1, 0)], resolved.Sources.Select(s => (s.Name, s.Rules, s.Overridden)));
    }

    [Fact]
    public void ResolveEffective_KeepsTheWinnerAndNamesWhatItOverrode()
    {
        var standard = new BpaRuleCollection(BpaRuleSourceKind.Machine, "standard", [Rule("A", "m"), Rule("DUP", "m")]);
        var team = new BpaRuleCollection(BpaRuleSourceKind.User, "team.json", [Rule("DUP", "u")], BpaRuleOrigin.Environment);

        var effective = BpaRuleResolver.ResolveEffective([standard, team]);

        Assert.Equal(["A", "DUP"], effective.Select(e => e.Rule.Id).Order());
        var dup = Assert.Single(effective, e => e.Rule.Id == "DUP");
        Assert.Same(team, dup.Source);
        Assert.Same(standard, Assert.Single(dup.Overrides));
        Assert.Empty(Assert.Single(effective, e => e.Rule.Id == "A").Overrides);
    }

    [Fact]
    public void ResolveEffective_External_EarlierEntryWins()
    {
        var first = new BpaRuleCollection(BpaRuleSourceKind.External, "first.json", [Rule("DUP", "1")], BpaRuleOrigin.Model);
        var second = new BpaRuleCollection(BpaRuleSourceKind.External, "second.json", [Rule("DUP", "2")], BpaRuleOrigin.Model);

        var dup = Assert.Single(BpaRuleResolver.ResolveEffective([first, second]));

        Assert.Same(first, dup.Source);
        Assert.Same(second, Assert.Single(dup.Overrides));
    }
}
