using Spectre.Console;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Output;

internal static class DidYouMean
{
    public static string? Suggest(string input, IReadOnlyList<string> candidates, int maxDistance = 3)
        => NameSuggestion.Closest(input, candidates, maxDistance);

    public static void WriteSuggestion(string input, IReadOnlyList<string> candidates)
    {
        var suggestion = Suggest(input, candidates);
        if (suggestion is null)
            return;

        var err = StdErr.Console();
        err.MarkupLine(Styling.Guidance($"Did you mean '{suggestion}'?"));
    }
}
