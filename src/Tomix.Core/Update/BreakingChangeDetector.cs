using System.Text.RegularExpressions;

namespace Tomix.Core.Update;

/// <summary>
/// Heuristic breaking-change detection over release-notes bodies. GitHub's auto-generated
/// notes render one bullet per PR title, so conventional-commit <c>!</c> markers
/// (<c>feat!:</c>, <c>refactor(app)!:</c>) survive into the notes verbatim.
/// </summary>
public static partial class BreakingChangeDetector
{
    // "* feat!: drop X by @u in #12" / "- fix(scope)!: ..." — the bang before the colon.
    [GeneratedRegex(@"^\s*[*-]?\s*\w+(\([^)]*\))?!:", RegexOptions.Multiline)]
    private static partial Regex ConventionalBangRegex();

    // Deliberately not the bare word "breaking": "non-breaking" would false-positive.
    [GeneratedRegex(@"breaking[ -]change|^#+\s*breaking\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BreakingPhraseRegex();

    public static bool IsBreaking(string? notesBody)
    {
        if (string.IsNullOrWhiteSpace(notesBody))
            return false;

        return ConventionalBangRegex().IsMatch(notesBody) || BreakingPhraseRegex().IsMatch(notesBody);
    }
}
