namespace Tomix.App.Find;

public sealed record FindModelResult(string Pattern, IReadOnlyList<FindMatch> Matches);

public sealed record FindMatch(
    string Path,
    string Type,
    string Name,
    string Property,
    string MatchedText,
    string Value,
    int Line,
    int Position);
