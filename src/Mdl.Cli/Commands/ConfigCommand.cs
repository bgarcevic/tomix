using System.CommandLine;
using Mdl.App.Config;
using Mdl.Cli.Output;

namespace Mdl.Cli.Commands;

internal sealed class ConfigCommand : ICommandModule
{
    public Command Build()
    {
        var command = new Command("config", "Manage local MDL configuration.");

        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildGet());
        command.Subcommands.Add(BuildSet());

        return command;
    }

    private static Command BuildList()
    {
        var format = OutputFormats.CreateOption();

        var command = new Command("list", "List all configuration values.")
        {
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var result = new ConfigHandler().List();
            return CommandOutput.Render(result, formatValue, RenderList);
        });

        return command;
    }

    private static Command BuildGet()
    {
        var keyArgument = new Argument<string>("key") { Description = "Configuration key." };
        var format = OutputFormats.CreateOption();

        var command = new Command("get", "Get a configuration value.")
        {
            keyArgument,
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var key = parseResult.GetValue(keyArgument) ?? "";
            var result = new ConfigHandler().Get(key);
            return CommandOutput.Render(result, formatValue, RenderGet);
        });

        return command;
    }

    private static Command BuildSet()
    {
        var keyArgument = new Argument<string>("key") { Description = "Configuration key." };
        var valueArgument = new Argument<string>("value") { Description = "Configuration value." };
        var format = OutputFormats.CreateOption();

        var command = new Command("set", "Set a configuration value.")
        {
            keyArgument,
            valueArgument,
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var key = parseResult.GetValue(keyArgument) ?? "";
            var value = parseResult.GetValue(valueArgument) ?? "";
            var result = new ConfigHandler().Set(key, value);
            return CommandOutput.Render(result, formatValue, RenderSet);
        });

        return command;
    }

    private static void RenderList(ConfigListResult result)
    {
        if (result.Values.Count == 0)
        {
            Console.WriteLine("No configuration values set.");
            return;
        }

        var nameWidth = result.Values.Keys.Max(key => key.Length);

        foreach (var (key, value) in result.Values)
            Console.WriteLine($"{key.PadRight(nameWidth)}  {value}");
    }

    private static void RenderGet(ConfigGetResult result)
        => Console.WriteLine(result.Value ?? "");

    private static void RenderSet(ConfigSetResult result)
        => Console.WriteLine($"{result.Key} = {result.Value}");
}
