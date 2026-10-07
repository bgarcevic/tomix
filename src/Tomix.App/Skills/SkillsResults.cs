namespace Tomix.App.Skills;

/// <summary>An agent harness tomix can install its skill for.</summary>
public enum SkillAgent
{
    Claude,
    Codex
}

/// <summary>Where a skill is installed: in this repository, or for the user everywhere.</summary>
public enum SkillScope
{
    Project,
    User
}

/// <summary>What is at one install location.</summary>
public enum SkillState
{
    /// <summary>Nothing there.</summary>
    NotInstalled,

    /// <summary>Installed by tx, unedited, and the same content this tx ships.</summary>
    Current,

    /// <summary>Installed by tx, unedited, from a different tomix version.</summary>
    Outdated,

    /// <summary>Installed by tx and edited since.</summary>
    Modified,

    /// <summary>A <c>tomix</c> skill folder that tx did not write.</summary>
    Unmanaged
}

/// <summary>What an install or uninstall did at one location.</summary>
public enum SkillAction
{
    Installed,
    Updated,
    Unchanged,
    Removed,
    NotInstalled,

    /// <summary>Left alone because the copy there was edited or not written by tx (see <c>--force</c>).</summary>
    Skipped
}

/// <param name="Agents">Agents to act on; empty means the default for the command.</param>
/// <param name="Scope">Project (this repository) or user (every project).</param>
/// <param name="Force">Overwrite or remove copies that were edited or not written by tx.</param>
public sealed record SkillsRequest(IReadOnlyList<SkillAgent> Agents, SkillScope Scope, bool Force = false);

/// <param name="InstalledVersion">The tomix version recorded in the installed copy, when tx wrote it.</param>
public sealed record SkillLocation(
    SkillAgent Agent,
    SkillScope Scope,
    string Path,
    SkillState State,
    string? InstalledVersion);

public sealed record SkillsStatusResult(string Skill, string Version, IReadOnlyList<SkillLocation> Locations);

public sealed record SkillChange(
    SkillAgent Agent,
    SkillScope Scope,
    string Path,
    SkillAction Action,
    SkillState Before);

public sealed record SkillsChangeResult(string Skill, string Version, IReadOnlyList<SkillChange> Changes);
