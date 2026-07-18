namespace Tomix.Core.Update;

/// <summary>
/// One published release of the CLI. <paramref name="Version"/> is normalized (no <c>v</c>
/// prefix); <paramref name="Body"/> carries the release notes as published.
/// </summary>
public sealed record ReleaseInfo(
    string Version,
    string? Name,
    string? Body,
    DateTimeOffset? PublishedAt,
    bool Prerelease);
