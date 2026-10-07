using Tomix.App.Skills;

namespace Tomix.App.Tests;

public sealed class SkillsHandlerTests
{
    private const string SkillMd = "---\nname: tomix\ndescription: Test skill.\n---\n\n# tomix\n";

    private static SkillBundle Bundle(string body = "# tomix\n", params (string Path, string Content)[] extra)
    {
        var files = new Dictionary<string, string>
        {
            [SkillBundle.EntryFile] = $"---\nname: tomix\ndescription: Test skill.\n---\n\n{body}"
        };
        foreach (var (path, content) in extra)
            files[path] = content;
        return new SkillBundle(files);
    }

    /// <summary>A git repository root (so the project scope resolves to it) and a separate home.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly TempDir _dir = new();

        public Sandbox()
        {
            Repo = _dir.CreateSubdirectory("repo");
            Directory.CreateDirectory(Path.Combine(Repo, ".git"));
            Home = _dir.CreateSubdirectory("home");
        }

        public string Repo { get; }
        public string Home { get; }

        public SkillsHandler Handler(SkillBundle bundle, string version = "1.0.0", string? cwd = null)
            => new(bundle, version, cwd ?? Repo, Home);

        public string ProjectSkill(string agentFolder) => Path.Combine(Repo, agentFolder, "skills", "tomix");

        public void Dispose() => _dir.Dispose();
    }

    private static SkillsRequest Project(params SkillAgent[] agents) => new(agents, SkillScope.Project);

    [Fact]
    public void Install_WithNoAgentSigns_InstallsForEveryAgent()
    {
        using var sandbox = new Sandbox();

        var result = sandbox.Handler(Bundle()).Install(Project());

        Assert.Equal(0, result.ExitCode);
        Assert.All(result.Data!.Changes, change => Assert.Equal(SkillAction.Installed, change.Action));
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectSkill(".agents"), "SKILL.md")));
    }

    [Theory]
    [InlineData("CLAUDE.md", SkillAgent.Claude)]
    [InlineData(".claude", SkillAgent.Claude)]
    [InlineData("AGENTS.md", SkillAgent.Codex)]
    [InlineData(".codex", SkillAgent.Codex)]
    public void Install_WithoutAgent_InstallsOnlyForDetectedAgent(string marker, SkillAgent expected)
    {
        using var sandbox = new Sandbox();
        if (marker.EndsWith(".md", StringComparison.Ordinal))
            File.WriteAllText(Path.Combine(sandbox.Repo, marker), "");
        else
            Directory.CreateDirectory(Path.Combine(sandbox.Repo, marker));

        var result = sandbox.Handler(Bundle()).Install(Project());

        var change = Assert.Single(result.Data!.Changes);
        Assert.Equal(expected, change.Agent);
    }

    [Fact]
    public void Install_StampsVersionAndReadsBackAsCurrent()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle(), version: "0.9.1");

        handler.Install(Project(SkillAgent.Claude));

        var markdown = File.ReadAllText(Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md"));
        Assert.StartsWith("---\nname: tomix\ndescription: Test skill.\nmetadata:\n  tomix-version: \"0.9.1\"\n", markdown);
        var location = handler.Status().Data!.Locations.Single(l => l is { Agent: SkillAgent.Claude, Scope: SkillScope.Project });
        Assert.Equal(SkillState.Current, location.State);
        Assert.Equal("0.9.1", location.InstalledVersion);
    }

    [Fact]
    public void Install_Twice_LeavesCurrentCopyUnchanged()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle());
        handler.Install(Project(SkillAgent.Codex));

        var result = handler.Install(Project(SkillAgent.Codex));

        Assert.Equal(SkillAction.Unchanged, Assert.Single(result.Data!.Changes).Action);
    }

    [Fact]
    public void Install_OverOlderBundle_UpdatesAndDropsFilesNoLongerShipped()
    {
        using var sandbox = new Sandbox();
        sandbox.Handler(Bundle("old\n", ("references/old.md", "old")), version: "1.0.0").Install(Project(SkillAgent.Claude));
        var newer = sandbox.Handler(Bundle("new\n"), version: "1.1.0");

        Assert.Equal(SkillState.Outdated, newer.Status().Data!.Locations[0].State);
        var result = newer.Install(Project(SkillAgent.Claude));

        Assert.Equal(SkillAction.Updated, Assert.Single(result.Data!.Changes).Action);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectSkill(".claude"), "references", "old.md")));
        Assert.Contains("new", File.ReadAllText(Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md")));
    }

    [Fact]
    public void Install_OverEditedCopy_SkipsWithConflictUnlessForced()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle());
        handler.Install(Project(SkillAgent.Claude));
        var skillFile = Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md");
        File.AppendAllText(skillFile, "\nMy team's rule.\n");

        var skipped = handler.Install(Project(SkillAgent.Claude));

        Assert.Equal(1, skipped.ExitCode);
        Assert.Equal(SkillAction.Skipped, Assert.Single(skipped.Data!.Changes).Action);
        Assert.Equal("TOMIX_SKILL_CONFLICT", Assert.Single(skipped.Diagnostics).Code);
        Assert.Contains("My team's rule.", File.ReadAllText(skillFile));

        var forced = handler.Install(Project(SkillAgent.Claude) with { Force = true });

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal(SkillAction.Updated, Assert.Single(forced.Data!.Changes).Action);
        Assert.DoesNotContain("My team's rule.", File.ReadAllText(skillFile));
    }

    [Fact]
    public void Install_ConflictAtOneLocation_StillInstallsTheOthers()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.ProjectSkill(".claude"));
        File.WriteAllText(Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md"), SkillMd);

        var result = sandbox.Handler(Bundle()).Install(Project(SkillAgent.Claude, SkillAgent.Codex));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [SkillAction.Skipped, SkillAction.Installed],
            result.Data!.Changes.Select(change => change.Action));
        Assert.Equal(SkillState.Unmanaged, result.Data.Changes[0].Before);
    }

    [Fact]
    public void Status_CrlfCheckoutOfInstalledCopy_IsNotReportedAsEdited()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle("line one\nline two\n", ("references/ci.md", "a\nb\n")));
        handler.Install(Project(SkillAgent.Claude));
        foreach (var file in Directory.EnumerateFiles(sandbox.ProjectSkill(".claude"), "*", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\n", "\r\n"));

        Assert.Equal(SkillState.Current, handler.Status().Data!.Locations[0].State);
    }

    [Fact]
    public void Status_AddedFileInInstalledCopy_ReadsAsEdited()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle());
        handler.Install(Project(SkillAgent.Claude));
        File.WriteAllText(Path.Combine(sandbox.ProjectSkill(".claude"), "notes.md"), "mine");

        Assert.Equal(SkillState.Modified, handler.Status().Data!.Locations[0].State);
    }

    [Fact]
    public void Uninstall_RemovesUneditedCopies_AndProtectsUnmanagedOnes()
    {
        using var sandbox = new Sandbox();
        var handler = sandbox.Handler(Bundle());
        handler.Install(Project(SkillAgent.Codex));
        Directory.CreateDirectory(sandbox.ProjectSkill(".claude"));
        File.WriteAllText(Path.Combine(sandbox.ProjectSkill(".claude"), "SKILL.md"), SkillMd);

        var result = handler.Uninstall(Project());

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [SkillAction.Skipped, SkillAction.Removed],
            result.Data!.Changes.Select(change => change.Action));
        Assert.True(Directory.Exists(sandbox.ProjectSkill(".claude")));
        Assert.False(Directory.Exists(sandbox.ProjectSkill(".agents")));

        var forced = handler.Uninstall(Project() with { Force = true });

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal(
            [SkillAction.Removed, SkillAction.NotInstalled],
            forced.Data!.Changes.Select(change => change.Action));
    }

    [Fact]
    public void ProjectScope_FromSubdirectory_ResolvesToRepositoryRoot()
    {
        using var sandbox = new Sandbox();
        var nested = Directory.CreateDirectory(Path.Combine(sandbox.Repo, "models", "sales")).FullName;

        sandbox.Handler(Bundle(), cwd: nested).Install(Project(SkillAgent.Claude));

        Assert.True(Directory.Exists(sandbox.ProjectSkill(".claude")));
        Assert.False(Directory.Exists(Path.Combine(nested, ".claude")));
    }

    [Fact]
    public void UserScope_InstallsUnderHome()
    {
        using var sandbox = new Sandbox();

        sandbox.Handler(Bundle()).Install(new SkillsRequest([SkillAgent.Codex], SkillScope.User));

        Assert.True(File.Exists(Path.Combine(sandbox.Home, ".agents", "skills", "tomix", "SKILL.md")));
        Assert.False(Directory.Exists(sandbox.ProjectSkill(".agents")));
    }

    /// <summary>
    /// The embedded bundle is <c>/skills/tomix</c>, file for file, and its frontmatter leaves
    /// <c>metadata</c> to the install stamp (a second <c>metadata</c> key would be invalid YAML).
    /// </summary>
    [Fact]
    public void EmbeddedBundle_MatchesSkillsFolderInRepository()
    {
        var source = RepoPaths.Combine("skills", "tomix");
        var onDisk = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(source, file).Replace('\\', '/'),
                file => File.ReadAllText(file).Replace("\r\n", "\n"));

        var embedded = SkillBundle.Embedded();

        Assert.Equal(onDisk.OrderBy(f => f.Key), embedded.Files.OrderBy(f => f.Key));
        Assert.Contains("references/ci.md", embedded.Files.Keys);
        Assert.DoesNotContain("\nmetadata:", embedded.Files[SkillBundle.EntryFile].Split("\n---\n")[0]);
        Assert.Contains("name: tomix\n", embedded.Files[SkillBundle.EntryFile]);
    }
}
