namespace Tomix.Core.Diagnostics;

/// <summary>
/// The "did you mean" behind every typo hint: the candidate name nearest to what was typed, by
/// case-insensitive edit distance. Callers choose how close counts.
/// </summary>
public static class NameSuggestion
{
    /// <summary>
    /// The candidate nearest to <paramref name="input"/> within <paramref name="maxDistance"/>
    /// edits; null when none is that close. A tie goes to the earliest candidate.
    /// </summary>
    public static string? Closest(string input, IEnumerable<string> candidates, int maxDistance)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Distance(input, candidate);
            if (distance < bestDistance && distance <= maxDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// A limit that grows with the name: a third of its length, at least 2. Suits model object
    /// and property names, where longer names tolerate more typos.
    /// </summary>
    public static int ScaledLimit(string input) => Math.Max(2, input.Length / 3);

    /// <summary>Case-insensitive Levenshtein distance between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
