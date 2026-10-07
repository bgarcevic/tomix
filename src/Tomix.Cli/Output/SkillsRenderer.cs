using Spectre.Console;
using Tomix.App.Skills;

namespace Tomix.Cli.Output;

internal static class SkillsRenderer
{
    public static void RenderStatus(SkillsStatusResult result)
    {
        var table = Styling.NewTable("Scope", "Agent", "State", "Path");
        foreach (var location in result.Locations)
        {
            table.AddRow(
                Styling.MarkupEscape(Label(location.Scope)),
                Styling.MarkupEscape(Label(location.Agent)),
                StateMarkup(location),
                Styling.Path(Display(location.Path)));
        }

        AnsiConsole.Write(table);

        if (result.Locations.Any(location => location.State == SkillState.Outdated))
            AnsiConsole.MarkupLine(Styling.Guidance("Update outdated copies with: tx skills install"));
    }

    public static void RenderInstall(SkillsChangeResult result)
    {
        RenderChanges(result);

        if (result.Changes.Any(change => change.Action is SkillAction.Installed or SkillAction.Updated))
            AnsiConsole.MarkupLine(Styling.Guidance("Start a new agent session to load the skill."));
    }

    public static void RenderUninstall(SkillsChangeResult result) => RenderChanges(result);

    private static void RenderChanges(SkillsChangeResult result)
    {
        foreach (var change in result.Changes)
        {
            var action = change.Action switch
            {
                SkillAction.Installed => Styling.Success("Installed"),
                SkillAction.Updated => Styling.Success("Updated  "),
                SkillAction.Removed => Styling.Success("Removed  "),
                SkillAction.Unchanged => Styling.Muted("Current  "),
                SkillAction.NotInstalled => Styling.Muted("Absent   "),
                _ => Styling.Warning("Skipped  ")
            };

            AnsiConsole.MarkupLine($"{action} {Styling.MarkupEscape(Label(change.Agent).PadRight(6))} {Styling.Path(Display(change.Path))}");
        }
    }

    private static string StateMarkup(SkillLocation location) => location.State switch
    {
        SkillState.Current => Styling.Success($"current ({location.InstalledVersion})"),
        SkillState.Outdated => Styling.Warning($"outdated ({location.InstalledVersion})"),
        SkillState.Modified => Styling.Warning("edited"),
        SkillState.Unmanaged => Styling.Warning("not installed by tx"),
        _ => Styling.Muted("not installed")
    };

    /// <summary>
    /// A path under the working directory reads relative to it, one under the home folder as
    /// <c>~/...</c>; JSON keeps the absolute path.
    /// </summary>
    private static string Display(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return relative.Replace('\\', '/');

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fromHome = Path.GetRelativePath(home, path);
        return !fromHome.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(fromHome)
            ? "~/" + fromHome.Replace('\\', '/')
            : path;
    }

    private static string Label(SkillScope scope) => scope == SkillScope.Project ? "project" : "user";

    private static string Label(SkillAgent agent) => agent == SkillAgent.Claude ? "claude" : "codex";
}
