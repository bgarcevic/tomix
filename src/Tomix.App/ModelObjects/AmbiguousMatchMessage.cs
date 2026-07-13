using Tomix.Core.Models;

namespace Tomix.App.ModelObjects;

/// <summary>
/// Shared TOMIX_OBJECT_AMBIGUOUS message: lists the candidate paths (capped) so the user can
/// pick one, instead of only reporting that the path was ambiguous.
/// </summary>
internal static class AmbiguousMatchMessage
{
    private const int MaxListed = 5;

    public const string Hint = "Disambiguate with -t <type> or a fuller path (e.g. 'Table/Object').";

    public static string For(string path, IReadOnlyList<ModelObject> matches)
    {
        var listed = matches
            .Take(MaxListed)
            .Select(m => $"{m.Path} ({ModelObjectProjection.KindLabel(m.Kind)})");
        var overflow = matches.Count > MaxListed ? $", … {matches.Count - MaxListed} more" : "";

        return $"Object path matched {matches.Count} objects: {path}. " +
               $"Candidates: {string.Join(", ", listed)}{overflow}.";
    }
}
