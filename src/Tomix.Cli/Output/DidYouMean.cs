using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class DidYouMean
{
    public static string? Suggest(string input, IReadOnlyList<string> candidates, int maxDistance = 3)
    {
        string? best = null;
        var bestDist = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var dist = Levenshtein(input, candidate);
            if (dist < bestDist && dist <= maxDistance)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    public static void WriteSuggestion(string input, IReadOnlyList<string> candidates)
    {
        var suggestion = Suggest(input, candidates);
        if (suggestion is null)
            return;

        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        err.MarkupLine(Styling.Guidance($"Did you mean '{Styling.MarkupEscape(suggestion)}'?"));
    }

    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
