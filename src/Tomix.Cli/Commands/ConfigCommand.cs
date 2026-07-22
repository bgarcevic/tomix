using System.CommandLine;
using Spectre.Console;
using Tomix.App.Config;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class ConfigCommand : ICommandModule
{
    private readonly TomixConfigStore _configStore;
    private readonly string _configDirectory;
    private readonly string _configFilePath;

    public ConfigCommand(TomixConfigStore configStore, string configDirectory, string configFilePath)
    {
        _configStore = configStore;
        _configDirectory = configDirectory;
        _configFilePath = configFilePath;
    }

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

            var result = new ConfigHandler(_configStore).Init(parseResult.GetValue(forceOption));
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
            var result = new ConfigHandler(_configStore).Set(key, value);
            return CommandOutput.Render(result, formatValue, RenderSet);
        });

        return command;
    }

    private Command BuildPaths()
    {
        var command = new Command("paths", "Show resolved paths for local CLI files.");

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "config paths", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(
                Tomix.Core.Results.TomixResult<ConfigPathsResult>.Ok(new ConfigPathsResult(_configDirectory, _configFilePath)),
                formatValue,
                result =>
                {
                    AnsiConsole.MarkupLine(Styling.KeyValue("configDir   ", result.ConfigDir));
                    AnsiConsole.MarkupLine(Styling.KeyValue("configFile  ", result.ConfigFile));
                });
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

            var result = new ConfigHandler(_configStore).List();
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
        {
            var suffix = result.UnsupportedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                ? " (unsupported)"
                : "";
            AnsiConsole.MarkupLine(Styling.KeyValue(key.PadRight(nameWidth) + " ", value + suffix));
        }
    }

    private static void RenderSet(ConfigSetResult result)
        => AnsiConsole.MarkupLine(Styling.Success($"{result.Key} = {result.Value}"));
}
