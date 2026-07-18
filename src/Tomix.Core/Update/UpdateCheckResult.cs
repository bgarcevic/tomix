namespace Tomix.Core.Update;

/// <summary>Result of <c>tx update --check</c>: what is installed, what is available, and the notes in between.</summary>
public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    InstallKind InstallKind,
    IReadOnlyList<ReleaseSummary> Releases);

/// <summary>One release between the installed and target versions, newest first.</summary>
public sealed record ReleaseSummary(
    string Version,
    DateTimeOffset? PublishedAt,
    bool Breaking,
    string? Notes);

/// <summary>Result of a performed update.</summary>
public sealed record UpdateApplyResult(
    string PreviousVersion,
    string NewVersion,
    InstallKind InstallKind,
    string Method);
