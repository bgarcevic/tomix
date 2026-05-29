using System.CommandLine;
using Mdl.App.Info;
using Mdl.Cli.Output;
using Mdl.Core.Models;

namespace Mdl.Cli.Commands;

internal sealed class InfoCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public InfoCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to the semantic model folder."
        };

        var format = OutputFormats.CreateOption();

        var command = new Command("info", "Show a summary of a semantic model.")
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

            var handler = new InfoModelHandler(_providers);
            var result = await handler.HandleAsync(new InfoModelRequest(new ModelReference(path)), cancellationToken);

            return CommandOutput.Render(result, formatValue, data => Render(data.Summary));
        });

        return command;
    }

    private static void Render(ModelSummary s)
    {
        Console.WriteLine(s.Name);
        Console.WriteLine();
        Console.WriteLine($"Compatibility level: {s.CompatibilityLevel}");
        Console.WriteLine($"Tables:              {s.Tables}");
        Console.WriteLine($"Columns:             {s.Columns}");
        Console.WriteLine($"Measures:            {s.Measures}");
        Console.WriteLine($"Relationships:       {s.Relationships}");
        Console.WriteLine($"Roles:               {s.Roles}");
    }
}
