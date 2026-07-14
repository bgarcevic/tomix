using System.CommandLine;
using Tomix.App.Macro;
using Tomix.Cli.Output;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class MacroCommand : ICommandModule
{
    public Command Build()
    {
        var macrosOption = CreateMacrosOption();

        var command = new Command("macro", "Manage and run macros against a model")
        {
            macrosOption
        };

        command.Subcommands.Add(BuildAdd(macrosOption));
        command.Subcommands.Add(BuildInit(macrosOption));
        command.Subcommands.Add(BuildList(macrosOption));
        command.Subcommands.Add(BuildRemove(macrosOption));
        command.Subcommands.Add(BuildRun(macrosOption));
        command.Subcommands.Add(BuildSet(macrosOption));
        command.Subcommands.Add(BuildSort(macrosOption));

        return command;
    }

    private static Command BuildInit(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite an existing macros file"
        };

        var command = new Command("init", "Create an empty macros file at the resolved path")
        {
            macrosOption,
            forceOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro init", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new MacroHandler().Init(
                MacroPath(parseResult, parentMacrosOption, macrosOption),
                parseResult.GetValue(forceOption));
            return CommandOutput.Render(
                result,
                format,
                data => AnsiConsole.MarkupLine(Styling.Success($"Created: {data.Path}")),
                data => new { created = data.Path });
        });

        return command;
    }

    private static Command BuildList(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var command = new Command("list", "List all macros")
        {
            macrosOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new MacroHandler().List(MacroPath(parseResult, parentMacrosOption, macrosOption));
            return CommandOutput.Render(result, format, RenderList, ToListJson);
        });

        return command;
    }

    private static Command BuildAdd(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var nameArgument = new Argument<string>("name")
        {
            Description = "Macro name"
        };
        var expressionOption = new Option<string?>("-e")
        {
            Description = "Inline C# code to execute"
        };
        var scriptOption = new Option<string?>("-s")
        {
            Description = "Path to script file to use as macro code"
        };
        var tooltipOption = new Option<string?>("--tooltip")
        {
            Description = "Tooltip text"
        };
        var contextsOption = new Option<string?>("--contexts")
        {
            Description = "Valid contexts"
        };
        var enabledOption = new Option<string?>("--enabled")
        {
            Description = "Enabled expression"
        };

        var command = new Command("add", "Add a new macro to the configured macros file")
        {
            nameArgument,
            macrosOption,
            expressionOption,
            scriptOption,
            tooltipOption,
            contextsOption,
            enabledOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro add", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new MacroHandler().Add(new MacroAddRequest(
                MacroPath(parseResult, parentMacrosOption, macrosOption),
                parseResult.GetValue(nameArgument) ?? "",
                parseResult.GetValue(expressionOption),
                parseResult.GetValue(scriptOption),
                parseResult.GetValue(tooltipOption),
                parseResult.GetValue(contextsOption),
                parseResult.GetValue(enabledOption)));

            return CommandOutput.Render(result, format, RenderSaved, ToSavedJson);
        });

        return command;
    }

    private static Command BuildRemove(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Macro name or numeric ID to remove"
        };

        var command = new Command("rm", "Remove a macro from the configured macros file")
        {
            nameOrIdArgument,
            macrosOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro rm", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new MacroHandler().Remove(
                MacroPath(parseResult, parentMacrosOption, macrosOption),
                parseResult.GetValue(nameOrIdArgument) ?? "");

            return CommandOutput.Render(
                result,
                format,
                RenderSaved,
                data => new { removed = data.Macro.Id, name = data.Macro.Name });
        });

        return command;
    }

    private static Command BuildSet(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Macro name or numeric ID to update"
        };
        var propertyOption = new Option<string?>("-q")
        {
            Description = "Property to set: name, execute, enabled, tooltip, validContexts"
        };
        var valueOption = new Option<string?>("-i")
        {
            Description = "New value for the property"
        };

        var command = new Command("set", "Set a macro property")
        {
            nameOrIdArgument,
            macrosOption,
            propertyOption,
            valueOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var property = parseResult.GetValue(propertyOption);
            var value = parseResult.GetValue(valueOption);
            if (string.IsNullOrWhiteSpace(property) || value is null)
            {
                Console.Error.WriteLine("Options -q and -i are required.");
                return 2;
            }

            var result = new MacroHandler().Set(
                MacroPath(parseResult, parentMacrosOption, macrosOption),
                parseResult.GetValue(nameOrIdArgument) ?? "",
                property,
                value);

            return CommandOutput.Render(
                result,
                format,
                data => AnsiConsole.MarkupLine(Styling.Success($"Updated: Macro {data.Macro.Id} ({data.Macro.Name}) .{property}")),
                data => new { updated = data.Macro.Id, property, value });
        });

        return command;
    }

    private static Command BuildSort(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var command = new Command("sort", "Sort macros by folder/name and re-assign sequential IDs")
        {
            macrosOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "macro sort", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new MacroHandler().Sort(MacroPath(parseResult, parentMacrosOption, macrosOption));
            return CommandOutput.Render(
                result,
                format,
                data => AnsiConsole.MarkupLine(Styling.Success($"Sorted: {data.Count} macro(s) re-ordered and re-numbered.")),
                data => new { sorted = data.Count, path = data.Path });
        });

        return command;
    }

    private static Command BuildRun(Option<string?> parentMacrosOption)
    {
        var macrosOption = CreateMacrosOption();
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Macro name or numeric ID to run"
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var onOption = new Option<string?>("--on");
        var forceOption = new Option<bool>("--force");
        var stageOption = new Option<bool>("--stage");
        var revertOption = new Option<bool>("--revert");
        var saveOption = new Option<bool>("--save");
        var saveToOption = new Option<string?>("--save-to");
        var serializationOption = new Option<string?>("--serialization");
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");

        var command = new Command("run", "Run a macro")
        {
            nameOrIdArgument,
            modelArgument,
            macrosOption,
            onOption,
            forceOption,
            stageOption,
            revertOption,
            saveOption,
            saveToOption,
            serializationOption
        };

        command.SetAction(_ =>
        {
            Console.Error.WriteLine("Command 'macro run' is not implemented yet.");
            return 1;
        });

        return command;
    }

    private static Option<string?> CreateMacrosOption()
        => new("--macros")
        {
            Description = "Path to a macros JSON file."
        };

    private static string? MacroPath(ParseResult parseResult, Option<string?> parentOption, Option<string?> localOption)
        => parseResult.GetValue(localOption) ?? parseResult.GetValue(parentOption);

    private static void RenderList(MacroListResult result)
    {
        if (result.Macros.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("No macros found."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.KeyValue("Source:", result.Path ?? ""));
        AnsiConsole.WriteLine();
        foreach (var macro in result.Macros)
        {
            var folder = string.IsNullOrWhiteSpace(macro.Folder) ? "" : $"{macro.Folder}\\";
            AnsiConsole.WriteLine($"{macro.Id}  {folder}{macro.DisplayName}");
        }

        AnsiConsole.MarkupLine(Styling.Muted($"{result.Macros.Count} macro(s)"));
    }

    private static object ToListJson(MacroListResult result)
        => new
        {
            path = result.Path,
            macros = result.Macros.Select(ToMacroDictionary).ToArray()
        };

    private static Dictionary<string, object?> ToMacroDictionary(MacroProjection macro)
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = macro.Id,
            ["name"] = macro.Name,
            ["displayName"] = macro.DisplayName
        };

        if (!string.IsNullOrWhiteSpace(macro.Folder))
            json["folder"] = macro.Folder;
        if (!string.IsNullOrWhiteSpace(macro.Enabled))
            json["enabled"] = macro.Enabled;
        if (!string.IsNullOrWhiteSpace(macro.Tooltip))
            json["tooltip"] = macro.Tooltip;
        if (!string.IsNullOrWhiteSpace(macro.ValidContexts))
            json["validContexts"] = macro.ValidContexts;

        return json;
    }

    private static void RenderSaved(MacroSavedResult result)
    {
        var verb = result.Action switch
        {
            "added" => "Added",
            "removed" => "Removed",
            _ => "Updated"
        };

        if (result.Action == "added")
            AnsiConsole.MarkupLine(Styling.Success($"{verb}: {result.Macro.Name} (ID: {result.Macro.Id}) to {result.Path}"));
        else
            AnsiConsole.MarkupLine(Styling.Success($"{verb}: Macro {result.Macro.Id} ({result.Macro.Name})"));
    }

    private static object ToSavedJson(MacroSavedResult result)
        => new
        {
            added = result.Macro.Name,
            id = result.Macro.Id,
            path = result.Path
        };
}
