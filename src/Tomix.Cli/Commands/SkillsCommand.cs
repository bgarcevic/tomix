using System.CommandLine;
using Tomix.App.Skills;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class SkillsCommand : ICommandModule
{
    private static readonly string[] AgentValues = ["claude", "codex", "all"];

    private readonly SkillBundle _bundle;
    private readonly string _version;
    private readonly Func<string> _workingDirectory;
    private readonly string _homeDirectory;

    public SkillsCommand(string version)
        : this(SkillBundle.Embedded(), version, () => Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal SkillsCommand(SkillBundle bundle, string version, Func<string> workingDirectory, string homeDirectory)
    {
        _bundle = bundle;
        _version = version;
        _workingDirectory = workingDirectory;
        _homeDirectory = homeDirectory;
    }

    public Command Build()
    {
        var command = new Command("skills", "Install the tomix skill for coding agents");

        command.Subcommands.Add(BuildInstall());
        command.Subcommands.Add(BuildStatus());
        command.Subcommands.Add(BuildUninstall());

        return command;
    }

    private Command BuildInstall()
    {
        var agentOption = AgentOption("Agent to install for: claude, codex, or all (default: the agents this repository or home folder uses, else all)");
        var userOption = UserOption("Install for every project in your home folder instead of this repository");
        var forceOption = new Option<bool>("--force") { Description = "Overwrite a copy that was edited or not installed by tx" };

        var command = new Command("install", "Install or update the skill for coding agents")
        {
            agentOption,
            userOption,
            forceOption
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "skills install", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = Handler().Install(Request(parseResult, agentOption, userOption, forceOption));
            return CommandOutput.Render(parseResult, result, formatValue, SkillsRenderer.RenderInstall);
        });

        return command;
    }

    private Command BuildStatus()
    {
        var command = new Command("status", "Show where the skill is installed and whether it is current");

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "skills status", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(parseResult, Handler().Status(), formatValue, SkillsRenderer.RenderStatus);
        });

        return command;
    }

    private Command BuildUninstall()
    {
        var agentOption = AgentOption("Agent to remove the skill for: claude, codex, or all (default: all)");
        var userOption = UserOption("Remove the copy in your home folder instead of this repository");
        var forceOption = new Option<bool>("--force") { Description = "Remove a copy that was edited or not installed by tx" };

        var command = new Command("uninstall", "Remove the skill installed by tx skills install")
        {
            agentOption,
            userOption,
            forceOption
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "skills uninstall", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = Handler().Uninstall(Request(parseResult, agentOption, userOption, forceOption));
            return CommandOutput.Render(parseResult, result, formatValue, SkillsRenderer.RenderUninstall);
        });

        return command;
    }

    private SkillsHandler Handler() => new(_bundle, _version, _workingDirectory(), _homeDirectory);

    private static SkillsRequest Request(
        ParseResult parseResult,
        Option<string?> agentOption,
        Option<bool> userOption,
        Option<bool> forceOption)
    {
        IReadOnlyList<SkillAgent> agents = parseResult.GetValue(agentOption)?.Trim().ToLowerInvariant() switch
        {
            "claude" => [SkillAgent.Claude],
            "codex" => [SkillAgent.Codex],
            "all" => Enum.GetValues<SkillAgent>(),
            _ => []
        };

        return new SkillsRequest(
            agents,
            parseResult.GetValue(userOption) ? SkillScope.User : SkillScope.Project,
            parseResult.GetValue(forceOption));
    }

    private static Option<string?> AgentOption(string description)
    {
        var option = new Option<string?>("--agent") { Description = description, HelpName = "agent" };
        option.AcceptAmongIgnoreCase(AgentValues);
        return option;
    }

    private static Option<bool> UserOption(string description)
        => new("--user") { Description = description };
}
