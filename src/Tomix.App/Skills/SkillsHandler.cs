using Tomix.Core.Diagnostics;
using Tomix.Core.Results;
using Tomix.Platform.Configuration;

namespace Tomix.App.Skills;

/// <summary>
/// Installs, inspects, and removes the tomix agent skill in the folders agent harnesses read:
/// <c>.claude/skills/</c> for Claude Code and <c>.agents/skills/</c> for Codex (and the other
/// harnesses that follow the Agent Skills layout), in the repository or in the user's home.
/// </summary>
/// <remarks>
/// A copy tx did not write, or one edited since tx wrote it, is never overwritten or deleted
/// without <c>Force</c>: that location is skipped with <c>TOMIX_SKILL_CONFLICT</c> and exit 1,
/// while the other locations still proceed.
/// </remarks>
public sealed class SkillsHandler
{
    private readonly SkillBundle _bundle;
    private readonly string _version;
    private readonly string _projectRoot;
    private readonly string _homeDirectory;

    /// <param name="workingDirectory">Resolved to its git repository root for the project scope.</param>
    public SkillsHandler(SkillBundle bundle, string version, string workingDirectory, string homeDirectory)
    {
        _bundle = bundle;
        _version = version;
        _projectRoot = SessionScope.FindRoot(workingDirectory);
        _homeDirectory = homeDirectory;
    }

    public TomixResult<SkillsStatusResult> Status()
    {
        var locations = (from scope in Enum.GetValues<SkillScope>()
                         from agent in Enum.GetValues<SkillAgent>()
                         select Inspect(agent, scope)).ToList();

        return TomixResult<SkillsStatusResult>.Ok(new SkillsStatusResult(SkillBundle.SkillName, _version, locations));
    }

    public TomixResult<SkillsChangeResult> Install(SkillsRequest request)
    {
        var agents = request.Agents.Count > 0 ? request.Agents : DetectAgents(request.Scope);
        var changes = new List<SkillChange>();
        var diagnostics = new List<TomixDiagnostic>();

        foreach (var agent in agents)
        {
            var location = Inspect(agent, request.Scope);

            if (location.State == SkillState.Current)
            {
                changes.Add(Change(location, SkillAction.Unchanged));
                continue;
            }

            if (IsProtected(location.State) && !request.Force)
            {
                changes.Add(Change(location, SkillAction.Skipped));
                diagnostics.Add(Conflict(location, "overwrite"));
                continue;
            }

            if (!TryWrite(location.Path, out var error))
                return WriteFailed<SkillsChangeResult>(location.Path, error);

            changes.Add(Change(location, location.State == SkillState.NotInstalled ? SkillAction.Installed : SkillAction.Updated));
        }

        return Finish(changes, diagnostics);
    }

    public TomixResult<SkillsChangeResult> Uninstall(SkillsRequest request)
    {
        var agents = request.Agents.Count > 0 ? request.Agents : Enum.GetValues<SkillAgent>();
        var changes = new List<SkillChange>();
        var diagnostics = new List<TomixDiagnostic>();

        foreach (var agent in agents)
        {
            var location = Inspect(agent, request.Scope);

            if (location.State == SkillState.NotInstalled)
            {
                changes.Add(Change(location, SkillAction.NotInstalled));
                continue;
            }

            if (IsProtected(location.State) && !request.Force)
            {
                changes.Add(Change(location, SkillAction.Skipped));
                diagnostics.Add(Conflict(location, "remove"));
                continue;
            }

            try
            {
                Directory.Delete(location.Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return WriteFailed<SkillsChangeResult>(location.Path, ex.Message);
            }

            changes.Add(Change(location, SkillAction.Removed));
        }

        return Finish(changes, diagnostics);
    }

    /// <summary>The skill folder for one agent and scope.</summary>
    public string PathFor(SkillAgent agent, SkillScope scope)
    {
        var root = scope == SkillScope.Project ? _projectRoot : _homeDirectory;
        var agentFolder = agent == SkillAgent.Claude ? ".claude" : ".agents";
        return Path.Combine(root, agentFolder, "skills", SkillBundle.SkillName);
    }

    /// <summary>
    /// Agents that show signs of use at this scope, plus any that already have the skill there;
    /// both when there are no signs at all, so a fresh repository still gets a working install.
    /// </summary>
    private IReadOnlyList<SkillAgent> DetectAgents(SkillScope scope)
    {
        var root = scope == SkillScope.Project ? _projectRoot : _homeDirectory;
        bool Exists(string name) => Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name));

        var detected = new List<SkillAgent>();
        if (Exists(".claude") || Exists("CLAUDE.md"))
            detected.Add(SkillAgent.Claude);
        if (Exists(".agents") || Exists(".codex") || Exists("AGENTS.md"))
            detected.Add(SkillAgent.Codex);

        return detected.Count > 0 ? detected : Enum.GetValues<SkillAgent>();
    }

    private SkillLocation Inspect(SkillAgent agent, SkillScope scope)
    {
        var path = PathFor(agent, scope);
        var entry = Path.Combine(path, SkillBundle.EntryFile);

        if (!Directory.Exists(path))
            return new SkillLocation(agent, scope, path, SkillState.NotInstalled, null);

        string? markdown = null;
        try
        {
            if (File.Exists(entry))
                markdown = File.ReadAllText(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as not ours: it is protected from overwrite without --force.
        }

        if (markdown is null || !SkillBundle.TryReadStamp(markdown, out var version, out var recordedHash))
            return new SkillLocation(agent, scope, path, SkillState.Unmanaged, null);

        var state = SkillBundle.HashOfInstalled(ReadInstalled(path)) != recordedHash
            ? SkillState.Modified
            : recordedHash == _bundle.Hash ? SkillState.Current : SkillState.Outdated;

        return new SkillLocation(agent, scope, path, state, version);
    }

    private static Dictionary<string, string> ReadInstalled(string path)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(path, file).Replace('\\', '/')] = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial read hashes differently from the recorded hash, so the copy reads as modified.
        }

        return files;
    }

    private bool TryWrite(string path, out string error)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true); // drop files a newer bundle no longer ships

            foreach (var (relative, content) in _bundle.Render(_version))
            {
                var target = Path.Combine(path, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
            }

            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsProtected(SkillState state) => state is SkillState.Modified or SkillState.Unmanaged;

    private static SkillChange Change(SkillLocation location, SkillAction action)
        => new(location.Agent, location.Scope, location.Path, action, location.State);

    private static TomixDiagnostic Conflict(SkillLocation location, string verb)
    {
        var reason = location.State == SkillState.Modified
            ? "was edited after tx installed it"
            : "was not installed by tx";
        return new TomixDiagnostic(
            Code: "TOMIX_SKILL_CONFLICT",
            Severity: DiagnosticSeverity.Error,
            Message: $"Did not {verb} {location.Path}: the skill there {reason}.",
            Hint: $"Keep your copy, or pass --force to {verb} it.");
    }

    private TomixResult<SkillsChangeResult> Finish(List<SkillChange> changes, List<TomixDiagnostic> diagnostics)
        => TomixResult<SkillsChangeResult>.Ok(
            new SkillsChangeResult(SkillBundle.SkillName, _version, changes),
            exitCode: diagnostics.Count > 0 ? 1 : 0,
            diagnostics: diagnostics);

    private static TomixResult<T> WriteFailed<T>(string path, string error)
        => TomixResult<T>.Fail(
            code: "TOMIX_SKILL_WRITE_FAILED",
            message: $"Could not write {path}: {error}",
            hint: "Check that the folder is writable.");
}
