using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Info;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class LoadCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public LoadCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model, Fabric path, or omit for active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("load", "Load a semantic model and display summary")
        {
            modelArgument
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(modelArgument);
            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? path,
                    _services.State,
                    out var source,
                    out var recentExit))
                return recentExit;
            var reference = RecentConnections.CreateResolver(source, _services.State).ResolveReference(source.Model, source.Database, source.Server);
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "load", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var handler = new InfoModelHandler(_providers);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => handler.HandleAsync(
                    new InfoModelRequest(reference),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(result, formatValue, Render, ToReferenceJson, errorFormat: errorFormat);
        });

        return command;
    }

    private static void Render(InfoModelResult result)
    {
        var summary = result.Summary;
        AnsiConsole.MarkupLine(Styling.Success($"Loaded: {summary.Name}"));
        if (summary.DatabaseName is null)
            AnsiConsole.MarkupLine(Styling.KeyValue("  name:          ", summary.Name));
        AnsiConsole.MarkupLine(Styling.KeyValue("  database:      ", summary.DatabaseName ?? ""));
        AnsiConsole.MarkupLine(Styling.KeyValue("  compatLevel:   ", summary.CompatibilityLevel.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("  tables:        ", summary.Tables.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("  measures:      ", summary.Measures.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("  columns:       ", summary.Columns.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("  relationships: ", summary.Relationships.ToString()));
    }

    private static object ToReferenceJson(InfoModelResult result)
    {
        var summary = result.Summary;
        return new
        {
            name = summary.Name,
            database = summary.DatabaseName,
            compatLevel = summary.CompatibilityLevel,
            tables = summary.Tables,
            measures = summary.Measures,
            columns = summary.Columns,
            relationships = summary.Relationships
        };
    }
}
