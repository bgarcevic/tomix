using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Config;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class ConfigCommand : ICommandModule
{
    private readonly AppServices _services;

    public ConfigCommand(AppServices services) => _services = services;

    public Command Build()
    {
        var command = new Command("config", "View and manage CLI configuration");

        command.Subcommands.Add(BuildInit());
        command.Subcommands.Add(BuildPaths());
        command.Subcommands.Add(BuildSet());
        command.Subcommands.Add(BuildShow());

        return command;
    }

    private Command BuildInit()
    {
        var forceOption = new Option<bool>("--force") { Description = "Overwrite existing config file" };

        var command = new Command("init", "Create a default config.json.")
        {
            forceOption
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "config init", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new ConfigHandler(_services.ConfigStore).Init(parseResult.GetValue(forceOption));
            return CommandOutput.Render(result, formatValue, RenderInit);
        });

        return command;
    }

    private Command BuildSet()
    {
        var keyArgument = new Argument<string>("key") { Description = "Configuration key." };
        var valueArgument = new Argument<string>("value") { Description = "Configuration value." };

        var command = new Command("set", "Set a configuration value.")
        {
            keyArgument,
            valueArgument
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "config set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var key = parseResult.GetValue(keyArgument) ?? "";
            var value = parseResult.GetValue(valueArgument) ?? "";
            var result = new ConfigHandler(_services.ConfigStore).Set(key, value);
            return CommandOutput.Render(result, formatValue, RenderSet);
        });

        return command;
    }

    private Command BuildPaths()
    {
        var command = new Command("paths", "Show resolved paths for local CLI files.");

        command.SetAction(_ =>
        {
            AnsiConsole.MarkupLine(Styling.KeyValue("configDir   ", _services.ConfigDirectory));
            AnsiConsole.MarkupLine(Styling.KeyValue("configFile  ", _services.ConfigFilePath));
            return 0;
        });

        return command;
    }

    private Command BuildShow()
    {
        var command = new Command("show", "Show current CLI configuration.");

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "config show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new ConfigHandler(_services.ConfigStore).List();
            return CommandOutput.Render(result, formatValue, RenderList);
        });

        return command;
    }

    private static void RenderInit(ConfigInitResult result)
        => AnsiConsole.MarkupLine(Styling.Path(result.Path));

    private static void RenderList(ConfigListResult result)
    {
        if (result.Values.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("No configuration values set."));
            return;
        }

        var nameWidth = result.Values.Keys.Max(key => key.Length);

        foreach (var (key, value) in result.Values)
            AnsiConsole.MarkupLine(Styling.KeyValue(key.PadRight(nameWidth) + " ", value));
    }

    private static void RenderSet(ConfigSetResult result)
        => AnsiConsole.MarkupLine(Styling.Success($"{result.Key} = {result.Value}"));
}
