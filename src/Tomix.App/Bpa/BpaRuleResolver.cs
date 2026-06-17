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

/// <summary>An ordered set of rules loaded from a single source.</summary>
public sealed record BpaRuleCollection(
    BpaRuleSourceKind Kind,
    string DisplayName,
    IReadOnlyList<BpaRule> Rules);

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
    {
        var winners = new Dictionary<string, Occurrence>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;

        foreach (var collection in collections)
        {
            foreach (var rule in collection.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                    continue;

                var candidate = new Occurrence(rule, collection.Kind, seq++);
                if (!winners.TryGetValue(rule.Id, out var current) || Wins(candidate, current))
                    winners[rule.Id] = candidate;
            }
        }

        return winners.Values
            .OrderBy(o => (int)o.Kind)
            .ThenBy(o => o.Seq)
            .Select(o => o.Rule)
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

    private readonly record struct Occurrence(BpaRule Rule, BpaRuleSourceKind Kind, int Seq);
}
