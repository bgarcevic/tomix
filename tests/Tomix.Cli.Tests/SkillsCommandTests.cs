using System.CommandLine;
using System.Text.Json;
using Tomix.App.Skills;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class SkillsCommandTests
{
    [Fact]
    public void Install_Json_ReportsEachLocation()
    {
        using var dir = new TempDir();
        var repo = dir.CreateSubdirectory("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var result = Invoke(Command(repo, dir.CreateSubdirectory("home")), "skills", "install", "--agent", "codex", "--output-format", "json");

        Assert.Equal(0, result.ExitCode);
        using var json = CommandJson.DataDocument(result.Stdout);
        Assert.Equal("tomix", json.RootElement.GetProperty("skill").GetString());
        var change = Assert.Single(json.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal("Codex", change.GetProperty("agent").GetString());
        Assert.Equal("Installed", change.GetProperty("action").GetString());
        Assert.True(File.Exists(Path.Combine(repo, ".agents", "skills", "tomix", "SKILL.md")));
    }

    [Fact]
    public void Install_OverUnmanagedCopy_ExitsOneWithConflictCode()
    {
        using var dir = new TempDir();
        var repo = dir.CreateSubdirectory("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        dir.WriteFile(Path.Combine("repo", ".claude", "skills", "tomix", "SKILL.md"), "---\nname: tomix\ndescription: mine\n---\n");

        var result = Invoke(Command(repo, dir.CreateSubdirectory("home")), "skills", "install", "--agent", "claude", "--error-format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("TOMIX_SKILL_CONFLICT", result.Stderr);
    }

    [Fact]
    public void Install_UnknownAgent_IsRejectedAtParseTime()
    {
        using var dir = new TempDir();

        var parse = TestRoot.With(Command(dir.Path, dir.Path)).Parse(["skills", "install", "--agent", "cursor"]);

        var error = Assert.Single(parse.Errors);
        Assert.Contains("Known values: claude, codex, all", error.Message);
    }

    [Fact]
    public void Status_Json_ListsEveryScopeAndAgent()
    {
        using var dir = new TempDir();

        var result = Invoke(Command(dir.Path, dir.CreateSubdirectory("home")), "skills", "status", "--output-format", "json");

        Assert.Equal(0, result.ExitCode);
        using var json = CommandJson.DataDocument(result.Stdout);
        var states = json.RootElement.GetProperty("locations").EnumerateArray()
            .Select(location => location.GetProperty("state").GetString())
            .ToList();
        Assert.Equal(4, states.Count);
        Assert.All(states, state => Assert.Equal("NotInstalled", state));
    }

    private static Command Command(string workingDirectory, string home)
        => new SkillsCommand(SkillBundle.Embedded(), "1.2.3", () => workingDirectory, home).Build();

    private static ConsoleCapture.Captured Invoke(Command command, params string[] args)
        => ConsoleCapture.Invoke(TestRoot.With(command).Parse(args), captureAnsiConsole: true);
}
