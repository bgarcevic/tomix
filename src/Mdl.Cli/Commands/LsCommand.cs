using System.CommandLine;
using Mdl.App.Ls;
using Mdl.Cli.Output;
using Mdl.Core.Models;

namespace Mdl.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private const char Esc = (char)27;

    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to the semantic model folder."
        };

        var format = OutputFormats.CreateOption();

        var command = new Command("ls", "List semantic model objects.")
        {
            modelArgument,
            format
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(modelArgument) ?? "";
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var handler = new LsModelHandler(_providers);
            var result = await handler.HandleAsync(new LsModelRequest(new ModelReference(path)), cancellationToken);

            return CommandOutput.Render(result, formatValue, data => Render(data.Inventory));
        });

        return command;
    }

    private static void Render(ModelInventory inventory)
    {
        var cyan = $"{Esc}[36m";
        var dim = $"{Esc}[2m";
        var reset = $"{Esc}[0m";

        Console.WriteLine($"{cyan}{inventory.Name}{reset}");
        Console.WriteLine();
        Console.WriteLine(
            $"{inventory.Tables} tables, " +
            $"{inventory.Measures} measures, " +
            $"{inventory.Columns} columns, " +
            $"{inventory.Relationships} relationships, " +
            $"{inventory.Roles} roles, " +
            $"{inventory.CalculationGroups} calc groups");
        Console.WriteLine();

        var nameWidth = Math.Max(
            "Table".Length,
            inventory.TableDetails.Count == 0 ? 0 : inventory.TableDetails.Max(t => t.Name.Length));

        Console.WriteLine($"{"Table".PadRight(nameWidth)}  {"Columns",7}  {"Measures",8}  {"Type",10}");

        foreach (var table in inventory.TableDetails)
        {
            var tableType = table.Calculated ? "calculated" : "regular";
            var line = $"{table.Name.PadRight(nameWidth)}  {table.Columns,7}  {table.Measures,8}  {tableType,10}";
            Console.WriteLine(table.Hidden ? $"{dim}{line}{reset}" : line);
        }
    }
}
