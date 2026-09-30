using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

/// <summary>
/// Where a rule collection came from. The numeric value is its precedence rank — higher wins when
/// the same rule id appears in more than one source (spec §6, lowest→highest):
/// additional &lt; machine &lt; user &lt; external &lt; model-embedded.
/// </summary>
public enum BpaRuleSourceKind
{
    Additional = 0,
    Machine = 1,
    User = 2,
    External = 3,
    ModelEmbedded = 4
}

/// <summary>
/// How a rule source was selected, for the attribution line (#233). Precedence is decided by
/// <see cref="BpaRuleSourceKind"/>; the origin only says which setting or option named the source.
/// </summary>
public enum BpaRuleOrigin
{
    /// <summary>A built-in or named <c>--ruleset</c> preset.</summary>
    Ruleset,

    /// <summary>The config-dir <c>bpa-rules.json</c> that <c>bpa rules add</c> writes.</summary>
    UserFile,

    /// <summary>The <c>bpa.rules</c> config key.</summary>
    Config,

    /// <summary>The <c>TOMIX_BPA_RULES</c> environment variable.</summary>
    Environment,

    /// <summary>A <c>--rules</c> option on the command line.</summary>
    Option,

    /// <summary>The model's own BestPracticeAnalyzer annotations (embedded or external files).</summary>
    Model
}

/// <summary>An ordered set of rules loaded from a single source.</summary>
public sealed record BpaRuleCollection(
    BpaRuleSourceKind Kind,
    string DisplayName,
    IReadOnlyList<BpaRule> Rules,
    BpaRuleOrigin Origin = BpaRuleOrigin.Ruleset);

/// <summary>One loaded source and how many of the effective rules it contributed.</summary>
/// <param name="Rules">Rules from this source that are in effect.</param>
/// <param name="Overridden">
/// Rules this source loaded that a higher-precedence source replaced, so a source whose rules were
/// all replaced (for example <c>standard</c> under <c>full</c>) reads as overridden, not empty.
/// </param>
public sealed record BpaRuleSourceSummary(
    string Name, BpaRuleSourceKind Kind, BpaRuleOrigin Origin, int Rules, int Overridden = 0);

/// <summary>The effective rule set plus a per-source account of where each rule came from.</summary>
public sealed record BpaResolvedRules(IReadOnlyList<BpaRule> Rules, IReadOnlyList<BpaRuleSourceSummary> Sources);

/// <summary>An effective rule, the collection it came from, and the collections it overrode.</summary>
public sealed record BpaEffectiveRule(
    BpaRule Rule, BpaRuleCollection Source, IReadOnlyList<BpaRuleCollection> Overrides);

/// <summary>
/// Combines rule collections from multiple sources into the effective rule set, comparing ids
/// case-insensitively and keeping the highest-precedence occurrence (spec §6). Within a kind, later
/// collections override earlier ones — except <see cref="BpaRuleSourceKind.External"/>, where
/// <em>earlier</em> entries win (spec §7). Output ordering is deterministic: by source rank, then
/// original encounter order.
/// </summary>
public static class BpaRuleResolver
{
    public static IReadOnlyList<BpaRule> Resolve(IEnumerable<BpaRuleCollection> collections)
        => ResolveWithSources(collections).Rules;

    /// <summary>
    /// Resolves like <see cref="Resolve"/> and credits each winning rule to the collection it came
    /// from. Every collection is listed in encounter order, even one whose rules were all
    /// overridden, so the attribution never hides a source that was loaded.
    /// </summary>
    public static BpaResolvedRules ResolveWithSources(IEnumerable<BpaRuleCollection> collections)
    {
        var sources = collections.ToList();
        var winners = new Dictionary<string, Occurrence>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;

        for (var index = 0; index < sources.Count; index++)
        {
            var collection = sources[index];
            foreach (var rule in collection.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                    continue;

                var candidate = new Occurrence(rule, collection.Kind, seq++, index);
                if (!winners.TryGetValue(rule.Id, out var current) || Wins(candidate, current))
                    winners[rule.Id] = candidate;
            }
        }

        var rules = winners.Values
            .OrderBy(o => (int)o.Kind)
            .ThenBy(o => o.Seq)
            .Select(o => o.Rule)
            .ToList();
        var counts = winners.Values.CountBy(o => o.Source).ToDictionary();
        var summaries = sources
            .Select((c, i) =>
            {
                var won = counts.GetValueOrDefault(i);
                var loaded = c.Rules
                    .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                    .Select(r => r.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return new BpaRuleSourceSummary(c.DisplayName, c.Kind, c.Origin, won, Overridden: loaded - won);
            })
            .ToList();

        return new BpaResolvedRules(rules, summaries);
    }

    /// <summary>
    /// Resolves like <see cref="Resolve"/> but keeps, for each effective rule, the collection it
    /// came from and every other collection that defined the same id, so a listing can show one
    /// row per rule and still say what it overrides.
    /// </summary>
    public static IReadOnlyList<BpaEffectiveRule> ResolveEffective(IEnumerable<BpaRuleCollection> collections)
    {
        var sources = collections.ToList();
        var winners = new Dictionary<string, Occurrence>(StringComparer.OrdinalIgnoreCase);
        var seenIn = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;

        for (var index = 0; index < sources.Count; index++)
        {
            foreach (var rule in sources[index].Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                    continue;

                var candidate = new Occurrence(rule, sources[index].Kind, seq++, index);
                if (!winners.TryGetValue(rule.Id, out var current) || Wins(candidate, current))
                    winners[rule.Id] = candidate;

                if (!seenIn.TryGetValue(rule.Id, out var seen))
                    seenIn[rule.Id] = seen = [];
                if (!seen.Contains(index))
                    seen.Add(index);
            }
        }

        return winners.Values
            .OrderBy(o => (int)o.Kind)
            .ThenBy(o => o.Seq)
            .Select(o => new BpaEffectiveRule(
                o.Rule,
                sources[o.Source],
                seenIn[o.Rule.Id].Where(i => i != o.Source).Select(i => sources[i]).ToList()))
            .ToList();
    }

    private static bool Wins(Occurrence candidate, Occurrence current)
    {
        if (candidate.Kind != current.Kind)
            return candidate.Kind > current.Kind;

        // Same kind: External keeps the earliest-encountered entry; every other kind lets a later
        // collection override an earlier one.
        return candidate.Kind == BpaRuleSourceKind.External
            ? candidate.Seq < current.Seq
            : candidate.Seq > current.Seq;
    }

    private readonly record struct Occurrence(BpaRule Rule, BpaRuleSourceKind Kind, int Seq, int Source);
}
