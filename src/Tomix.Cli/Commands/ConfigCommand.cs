using System.CommandLine;
using Tomix.App.Config;
using Tomix.Cli.Output;
using Tomix.Core.Configuration;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class ConfigCommand : ICommandModule
{
    public Command Build()
    {
        var command = new Command("config", "View and manage CLI configuration");

        command.Subcommands.Add(BuildInit());
        command.Subcommands.Add(BuildPaths());
        command.Subcommands.Add(BuildSet());
        command.Subcommands.Add(BuildShow());

        return command;
    }

    private static Command BuildInit()
    {
        var forceOption = new Option<bool>("--force") { Description = "Overwrite existing config file" };

        var command = new Command("init", "Create a default config.json.")
        {
            forceOption
        };

        command.SetAction(parseResult =>
        {
            Directory.CreateDirectory(TomixPaths.ConfigDirectory);
            if (parseResult.GetValue(forceOption) || !File.Exists(TomixPaths.ConfigFile))
                File.WriteAllText(TomixPaths.ConfigFile, "{\n}\n");

            AnsiConsole.MarkupLine(Styling.Path(TomixPaths.ConfigFile));
            return 0;
        });

        return command;
    }

    private static Command BuildSet()
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
            var result = new ConfigHandler().Set(key, value);
            return CommandOutput.Render(result, formatValue, RenderSet);
        });

        return command;
    }

    private static Command BuildPaths()
    {
        var command = new Command("paths", "Show resolved paths for local CLI files.");

        command.SetAction(_ =>
        {
            AnsiConsole.MarkupLine(Styling.KeyValue("configDir   ", TomixPaths.ConfigDirectory));
            AnsiConsole.MarkupLine(Styling.KeyValue("configFile  ", TomixPaths.ConfigFile));
            return 0;
        });

        return command;
    }

    private static Command BuildShow()
    {
        var command = new Command("show", "Show current CLI configuration.");

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "config show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new ConfigHandler().List();
            return CommandOutput.Render(result, formatValue, RenderList);
        });

        return command;
    }

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
